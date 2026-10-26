using System.Runtime.InteropServices;

namespace SensorDeck.App.Services;

/// <summary>A rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;
}

/// <summary>
/// A Windows "application desktop toolbar" (the mechanism the taskbar uses): registers a window, optionally reserves a
/// strip along a monitor edge so other windows (and maximizing) stay clear of it, and reports fullscreen apps.
/// All coordinates are physical pixels.
/// </summary>
/// <remarks>See SHAppBarMessage in the Windows documentation. Call from the window's UI thread.</remarks>
public sealed class AppBar : IDisposable
{
    private const uint AbmNew = 0;
    private const uint AbmRemove = 1;
    private const uint AbmQueryPos = 2;
    private const uint AbmSetPos = 3;
    private const uint AbeLeft = 0;
    private const uint AbeRight = 2;

    /// <summary>wParam of <see cref="CallbackMessage"/>: another app bar or the taskbar moved; dock again.</summary>
    public const int NotifyPosChanged = 1;

    /// <summary>wParam of <see cref="CallbackMessage"/>: a fullscreen app opened (lParam ≠ 0) or closed (lParam = 0).</summary>
    public const int NotifyFullscreenApp = 2;

    private static readonly HashSet<AppBar> Live = [];
    private readonly IntPtr _hwnd;
    private bool _registered;

    static AppBar()
    {
        // Best effort on a crash: give the reserved space back (normal exits remove the bar when the window closes).
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAll();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => RemoveAll();
    }

    public AppBar(IntPtr hwnd)
    {
        _hwnd = hwnd;
        CallbackMessage = (int)RegisterWindowMessage("SensorDeckSidebarAppBar");
        TaskbarCreatedMessage = (int)RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>The window message Windows sends this bar's window for notifications.</summary>
    public int CallbackMessage { get; }

    /// <summary>Broadcast when Explorer (re)starts: every app bar registration was lost and must be redone.</summary>
    public int TaskbarCreatedMessage { get; }

    /// <summary>After an Explorer restart: forget the lost registration so <see cref="Register"/> registers again.</summary>
    public void Reset() => _registered = false;

    /// <summary>
    /// Registers the bar (needed for fullscreen notifications even when no space is reserved). Returns false if
    /// Windows refused.
    /// </summary>
    public bool Register()
    {
        if (_registered)
        {
            return true;
        }

        var data = Data();
        data.uCallbackMessage = (uint)CallbackMessage;
        _registered = SHAppBarMessage(AbmNew, ref data) != UIntPtr.Zero;
        if (_registered)
        {
            lock (Live)
            {
                Live.Add(this);
            }
        }

        return _registered;
    }

    /// <summary>
    /// Reserves a strip <paramref name="widthPx"/> wide along the left or right of <paramref name="monitor"/> (its full
    /// bounds), leaving room for the taskbar and other bars. Returns the rectangle the window should occupy.
    /// </summary>
    public PixelRect Reserve(PixelRect monitor, bool rightEdge, int widthPx)
    {
        Register();
        var data = Data();
        data.uEdge = rightEdge ? AbeRight : AbeLeft;
        data.rc = StripRect(monitor, rightEdge, widthPx);

        // Windows moves the proposed rectangle off other bars (a taskbar docked on the same side, for example)…
        SHAppBarMessage(AbmQueryPos, ref data);

        // …which may change its width; keep ours, measured from the edge it settled on.
        if (rightEdge)
        {
            data.rc.Left = data.rc.Right - widthPx;
        }
        else
        {
            data.rc.Right = data.rc.Left + widthPx;
        }

        SHAppBarMessage(AbmSetPos, ref data);
        return new PixelRect(data.rc.Left, data.rc.Top, data.rc.Right, data.rc.Bottom);
    }

    /// <summary>Gives back any reserved space (the bar stays registered for notifications).</summary>
    public void Release()
    {
        if (!_registered)
        {
            return;
        }

        // Removing and re-adding is the documented way to drop a reservation.
        var data = Data();
        SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
        Register();
    }

    public void Dispose()
    {
        lock (Live)
        {
            Live.Remove(this);
        }

        if (!_registered)
        {
            return;
        }

        var data = Data();
        SHAppBarMessage(AbmRemove, ref data);
        _registered = false;
    }

    private static void RemoveAll()
    {
        AppBar[] bars;
        lock (Live)
        {
            bars = [.. Live];
        }

        foreach (var bar in bars)
        {
            bar.Dispose();
        }
    }

    /// <summary>The strip along one side of a rectangle.</summary>
    public static PixelRect Strip(PixelRect area, bool rightEdge, int widthPx) => rightEdge
        ? new PixelRect(area.Right - widthPx, area.Top, area.Right, area.Bottom)
        : new PixelRect(area.Left, area.Top, area.Left + widthPx, area.Bottom);

    /// <summary>True when the foreground window covers the whole of <paramref name="monitor"/> (a fullscreen game or video).</summary>
    public static bool IsFullscreenOn(PixelRect monitor)
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == GetShellWindow() || foreground == GetDesktopWindow()
            || IsDesktopClass(foreground) || !GetWindowRect(foreground, out var r))
        {
            return false; // clicking the desktop (or Win+D) also sends "fullscreen app opened"
        }

        return r.Left <= monitor.Left && r.Top <= monitor.Top && r.Right >= monitor.Right && r.Bottom >= monitor.Bottom;
    }

    private static bool IsDesktopClass(IntPtr hwnd)
    {
        var name = new char[64];
        var length = GetClassName(hwnd, name, name.Length);
        var text = new string(name, 0, Math.Max(0, length));
        return text is "WorkerW" or "Progman";
    }

    private Appbardata Data() => new() { cbSize = Marshal.SizeOf<Appbardata>(), hWnd = _hwnd };

    private static Rect StripRect(PixelRect monitor, bool rightEdge, int widthPx)
    {
        var s = Strip(monitor, rightEdge, widthPx);
        return new Rect { Left = s.Left, Top = s.Top, Right = s.Right, Bottom = s.Bottom };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Appbardata
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref Appbardata pData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string lpString);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);
}
