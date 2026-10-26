using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using CommunityToolkit.Mvvm.ComponentModel;

namespace SensorDeck.App.Services;

/// <summary>
/// System-wide shortcuts that work even while a game has focus: show/hide every dashboard window (default
/// Ctrl+Alt+D) and turn click-through on/off for them (default Ctrl+Shift+Alt+C). Uses RegisterHotKey on a hidden
/// message-only window, so they work while SensorDeck sits in the tray.
/// </summary>
public sealed partial class GlobalHotkeyService : ObservableObject, IDisposable
{
    private const int OverlayId = 0x5D01;
    private const int ClickThroughId = 0x5D02;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1;
    private const uint ModControl = 0x2;
    private const uint ModShift = 0x4;
    private const uint ModWin = 0x8;
    private const uint ModNoRepeat = 0x4000;

    private readonly AppSettings _settings;
    private readonly DialogService _dialogs;
    private readonly HashSet<int> _registered = [];
    private HwndSource? _window;

    /// <summary>Show/hide shortcut state: "Ctrl+Alt+D …", "… is used by another app", or "Off".</summary>
    [ObservableProperty]
    private string _status = "Off";

    [ObservableProperty]
    private string _clickThroughStatus = "Off";

    public GlobalHotkeyService(AppSettings settings, DialogService dialogs)
    {
        _settings = settings;
        _dialogs = dialogs;
    }

    /// <summary>Registers the saved shortcuts (call once at startup, on the UI thread).</summary>
    public void Start()
    {
        // Keep the saved shortcuts even if another app holds one right now (it may be free next time).
        Register(OverlayId, _settings.OverlayHotkey, "", "shows/hides dashboard windows from anywhere", out var status, out _);
        Status = status;
        Register(ClickThroughId, _settings.ClickThroughHotkey, "", "turns click-through on/off for dashboard windows", out var clickStatus, out _);
        ClickThroughStatus = clickStatus;
    }

    /// <summary>Replaces the show/hide shortcut. False (old one kept) when it's invalid or taken.</summary>
    public bool Apply(string? text)
    {
        var ok = Register(OverlayId, text, _settings.OverlayHotkey, "shows/hides dashboard windows from anywhere", out var status, out var saved);
        _settings.OverlayHotkey = saved;
        _settings.Save();
        Status = status;
        return ok;
    }

    /// <summary>Replaces the click-through shortcut.</summary>
    public bool ApplyClickThrough(string? text)
    {
        var ok = Register(ClickThroughId, text, _settings.ClickThroughHotkey, "turns click-through on/off for dashboard windows", out var status, out var saved);
        _settings.ClickThroughHotkey = saved;
        _settings.Save();
        ClickThroughStatus = status;
        return ok;
    }

    /// <summary>"Ctrl+Alt+D" → modifiers + key. At least one modifier is required.</summary>
    public static bool TryParse(string text, out uint modifiers, out Key key)
    {
        modifiers = 0;
        key = Key.None;
        var parts = (text ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        foreach (var part in parts[..^1])
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL" or "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "ALT":
                    modifiers |= ModAlt;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "WIN" or "WINDOWS":
                    modifiers |= ModWin;
                    break;
                default:
                    return false;
            }
        }

        var last = parts[^1];
        if (last.Length == 1 && char.IsDigit(last[0]))
        {
            last = "D" + last;
        }

        // IsDefined: Enum.TryParse also accepts any number ("Ctrl+Alt+999"), which isn't a real key.
        return Enum.TryParse(last, ignoreCase: true, out key) && Enum.IsDefined(key) && key != Key.None && modifiers != 0;
    }

    public void Dispose()
    {
        foreach (var id in _registered.ToList())
        {
            Unregister(id);
        }

        _window?.Dispose();
        _window = null;
    }

    /// <summary>
    /// Registers <paramref name="text"/> under <paramref name="id"/>. On failure the previous shortcut is registered
    /// again and kept (returned in <paramref name="saved"/>).
    /// </summary>
    private bool Register(int id, string? text, string previous, string purpose, out string status, out string saved)
    {
        _window ??= CreateWindow();
        var wanted = text?.Trim() ?? "";
        Unregister(id);
        if (wanted.Length == 0)
        {
            status = "Off";
            saved = "";
            return true;
        }

        if (!TryParse(wanted, out var modifiers, out var key))
        {
            status = $"“{wanted}” isn't a shortcut. Use e.g. Ctrl+Alt+D (Ctrl, Alt, Shift or Win plus a key).";
            saved = Reregister(id, previous, wanted) ? previous : "";
            return false;
        }

        var other = id == OverlayId ? _settings.ClickThroughHotkey : _settings.OverlayHotkey;
        if (TryParse(other, out var otherModifiers, out var otherKey) && otherModifiers == modifiers && otherKey == key)
        {
            var otherName = id == OverlayId ? "click-through" : "show/hide";
            status = $"{wanted} is already the {otherName} shortcut. To swap them, clear that one first.";
            saved = Reregister(id, previous, wanted) ? previous : "";
            return false;
        }

        if (!RegisterHotKey(_window.Handle, id, modifiers | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            status = $"{wanted} is already used by another app; pick another.";
            saved = Reregister(id, previous, wanted) ? previous : "";
            return false;
        }

        _registered.Add(id);
        status = $"{wanted} {purpose}.";
        saved = wanted;
        return true;
    }

    private bool Reregister(int id, string previous, string rejected)
    {
        if (previous.Length == 0 || previous == rejected || _window is null || !TryParse(previous, out var modifiers, out var key))
        {
            return false;
        }

        if (RegisterHotKey(_window.Handle, id, modifiers | ModNoRepeat, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            _registered.Add(id);
            return true;
        }

        return false;
    }

    private HwndSource CreateWindow()
    {
        // HWND_MESSAGE parent: a message-only window, never shown.
        var window = new HwndSource(new HwndSourceParameters("SensorDeck hotkeys") { ParentWindow = new IntPtr(-3), Width = 0, Height = 0 });
        window.AddHook(WndProc);
        return window;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmHotkey)
        {
            return IntPtr.Zero;
        }

        switch (wParam.ToInt32())
        {
            case OverlayId:
                _dialogs.ToggleDashboardWindows();
                handled = true;
                break;
            case ClickThroughId:
                _dialogs.ToggleClickThrough();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void Unregister(int id)
    {
        if (_registered.Remove(id) && _window is not null)
        {
            UnregisterHotKey(_window.Handle, id);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
