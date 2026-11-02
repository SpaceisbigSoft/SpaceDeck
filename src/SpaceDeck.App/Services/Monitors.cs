using System.Globalization;
using System.Runtime.InteropServices;

namespace SpaceDeck.App.Services;

/// <summary>A connected monitor, in physical pixels.</summary>
/// <param name="DeviceName">Windows' device name (e.g. \\.\DISPLAY2), used to remember the choice.</param>
public sealed record MonitorInfo(string DeviceName, string Label, PixelRect Bounds, PixelRect WorkArea, bool IsPrimary);

/// <summary>Lists monitors and their scaling. Names use Windows' device numbers (usually, not always, those in Settings → Display).</summary>
public static class Monitors
{
    public static IReadOnlyList<MonitorInfo> All()
    {
        var result = new List<MonitorInfo>();
        foreach (var screen in System.Windows.Forms.Screen.AllScreens.OrderBy(s => s.Bounds.Left).ThenBy(s => s.Bounds.Top))
        {
            var b = screen.Bounds;
            var w = screen.WorkingArea;
            var digits = screen.DeviceName.Length;
            while (digits > 0 && char.IsDigit(screen.DeviceName[digits - 1]))
            {
                digits--;
            }

            var number = screen.DeviceName[digits..];
            var name = number.Length > 0 ? $"Display {number}" : screen.DeviceName;
            var label = string.Create(CultureInfo.CurrentCulture, $"{name} · {b.Width}×{b.Height}{(screen.Primary ? " (main)" : "")}");
            result.Add(new MonitorInfo(
                screen.DeviceName,
                label,
                new PixelRect(b.Left, b.Top, b.Right, b.Bottom),
                new PixelRect(w.Left, w.Top, w.Right, w.Bottom),
                screen.Primary));
        }

        return result;
    }

    /// <summary>The monitor with this device name, or the main one if it isn't connected (or none was chosen).</summary>
    public static MonitorInfo Find(string? deviceName)
    {
        var all = All();
        return all.FirstOrDefault(m => string.Equals(m.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(m => m.IsPrimary)
            ?? all[0];
    }

    /// <summary>The monitor's current work area (without the taskbar and reserved bars), read live from Windows.</summary>
    public static PixelRect WorkAreaOf(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var info = new MonitorInfoNative { cbSize = Marshal.SizeOf<MonitorInfoNative>() };
        var handle = MonitorFromPoint(CenterOf(monitor), MonitorDefaultToNearest);
        return GetMonitorInfo(handle, ref info)
            ? new PixelRect(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom)
            : monitor.WorkArea;
    }

    /// <summary>The monitor's scale factor (1.0 = 100 %, 1.5 = 150 %).</summary>
    public static double ScaleOf(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        try
        {
            var handle = MonitorFromPoint(CenterOf(monitor), MonitorDefaultToNearest);
            return GetDpiForMonitor(handle, MdtEffectiveDpi, out var dpiX, out _) == 0 && dpiX > 0 ? dpiX / 96.0 : 1.0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1.0;
        }
    }

    private static Point CenterOf(MonitorInfo monitor) => new()
    {
        X = monitor.Bounds.Left + (monitor.Bounds.Width / 2),
        Y = monitor.Bounds.Top + (monitor.Bounds.Height / 2),
    };

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfoNative
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfoNative lpmi);
    private const int MdtEffectiveDpi = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Point pt, uint dwFlags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
}
