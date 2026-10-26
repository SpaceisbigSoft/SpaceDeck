using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SensorDeck.App.Services;
using SensorDeck.App.Themes;
using SensorDeck.App.ViewModels;

namespace SensorDeck.App.Views;

/// <summary>
/// A dashboard in its own borderless window: full screen, a second monitor, or a small always-on-top overlay.
/// Position, size, on-top and opacity are kept in <see cref="DashboardWindowState"/> and restored next time.
/// </summary>
public partial class DashboardWindow : Window
{
    private readonly DashboardWindowState _state;
    private readonly Action _save;
    private readonly Func<IReadOnlyList<DashboardDocumentViewModel>> _all;
    private readonly DispatcherTimer _rotate = new();

    /// <param name="home">The dashboard this window was opened for (its placement is saved under it).</param>
    /// <param name="all">Every dashboard, for ←/→, 1–9 and rotation.</param>
    public DashboardWindow(DashboardWindowState state, Action save, DashboardDocumentViewModel home, Func<IReadOnlyList<DashboardDocumentViewModel>> all)
    {
        ArgumentNullException.ThrowIfNull(state);
        _state = state;
        _save = save;
        _all = all;
        Home = home;
        DataContext = home;
        InitializeComponent();
        Appearance.Attach(this);
        GlassEffect.Attach(this);
        Restore();
        ContentSize.FillMenu(SizeMenu, () => _state.FitToHeight, () => DashCanvas.EffectiveScale, SetContentSize);
        _rotate.Tick += (_, _) => Step(1);
        SourceInitialized += (_, _) => ApplyClickThrough();
        SetRotation(_state.RotateSeconds, save: false);
        Closed += (_, _) => _rotate.Stop();
    }

    /// <summary>The dashboard this window belongs to (it may be showing another one while rotating).</summary>
    public DashboardDocumentViewModel Home { get; }

    /// <summary>Clicks pass through to the window underneath (a game). Turned off from the tray or the hotkey.</summary>
    public bool ClickThrough
    {
        get => _state.ClickThrough;
        set
        {
            _state.ClickThrough = value;
            ClickThroughItem.IsChecked = value;
            ApplyClickThrough();
            if (value)
            {
                SetTopmost(true); // an overlay you can't click must stay visible (also saves)
            }
            else
            {
                _save();
            }
        }
    }

    private void ApplyClickThrough()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLongPtr(handle, GwlExStyle).ToInt64();
        style = _state.ClickThrough ? style | WsExTransparent | WsExLayered : style & ~WsExTransparent;
        SetWindowLongPtr(handle, GwlExStyle, new IntPtr(style));
        Chrome.Visibility = _state.ClickThrough ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ClickThrough_Click(object sender, RoutedEventArgs e) => ClickThrough = ClickThroughItem.IsChecked;

    private const int GwlExStyle = -20;
    private const long WsExTransparent = 0x20;
    private const long WsExLayered = 0x80000;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    /// <summary>Shows another dashboard in this window.</summary>
    public void ShowDashboard(DashboardDocumentViewModel dashboard) => DataContext = dashboard;

    /// <summary>Next (+1) or previous (−1) dashboard, wrapping around.</summary>
    private void Step(int delta)
    {
        var all = _all();
        if (all.Count == 0)
        {
            return;
        }

        var index = DataContext is DashboardDocumentViewModel current ? IndexOf(all, current) : -1;
        ShowDashboard(all[((index + delta) % all.Count + all.Count) % all.Count]);
    }

    private static int IndexOf(IReadOnlyList<DashboardDocumentViewModel> all, DashboardDocumentViewModel item)
    {
        for (var i = 0; i < all.Count; i++)
        {
            if (ReferenceEquals(all[i], item))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Longest rotation interval (a day); also keeps a hand-edited value inside what DispatcherTimer accepts.</summary>
    private const int MaxRotateSeconds = 24 * 3600;

    private void SetRotation(int seconds, bool save = true)
    {
        _state.RotateSeconds = Math.Clamp(seconds, 0, MaxRotateSeconds);
        _rotate.Stop();
        if (_state.RotateSeconds > 0)
        {
            _rotate.Interval = TimeSpan.FromSeconds(_state.RotateSeconds);
            _rotate.Start();
        }

        foreach (var item in RotateMenu.Items.OfType<MenuItem>())
        {
            item.IsChecked = item.Tag is string tag && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s == _state.RotateSeconds;
        }

        if (save)
        {
            _save();
        }
    }

    private void Rotate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
        {
            SetRotation(seconds);
        }
    }

    private void Next_Click(object sender, RoutedEventArgs e) => Step(1);

    private void Previous_Click(object sender, RoutedEventArgs e) => Step(-1);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.F11:
                ToggleFullScreen();
                e.Handled = true;
                break;
            case Key.T:
                SetTopmost(!Topmost);
                e.Handled = true;
                break;
            case Key.Right or Key.PageDown:
                Step(1);
                e.Handled = true;
                break;
            case Key.Left or Key.PageUp:
                Step(-1);
                e.Handled = true;
                break;
            case >= Key.D1 and <= Key.D9:
                var all = _all();
                var index = e.Key - Key.D1;
                if (index < all.Count)
                {
                    ShowDashboard(all[index]);
                }

                e.Handled = true;
                break;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        Remember();

        // Closed by the user: don't reopen at startup. Closed because the app is exiting: reopen next time.
        _state.Open = AppLifetime.IsExiting;
        _save();
    }

    private void Restore()
    {
        Topmost = _state.Topmost;
        TopmostItem.IsChecked = _state.Topmost;
        ClickThroughItem.IsChecked = _state.ClickThrough;
        if (_state.ClickThrough)
        {
            Topmost = true;
            TopmostItem.IsChecked = true;
        }
        Opacity = Math.Clamp(_state.Opacity, 0.4, 1);
        DashCanvas.SetContentSize(_state.FitToHeight, _state.ContentScale);

        // Only reuse the saved position if it's still on a screen (monitors change).
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var saved = new Rect(
            _state.Left,
            _state.Top,
            Math.Clamp(_state.Width, MinWidth, Math.Max(MinWidth, screen.Width)),
            Math.Clamp(_state.Height, MinHeight, Math.Max(MinHeight, screen.Height)));
        var visible = Rect.Intersect(screen, saved);
        if (_state.HasPosition && !visible.IsEmpty && visible.Width >= 80 && visible.Height >= 80)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = saved.Left;
            Top = saved.Top;
            Width = saved.Width;
            Height = saved.Height;
        }

        if (_state.Maximized)
        {
            // Maximize on the monitor the window was on, once it exists there.
            SourceInitialized += (_, _) => WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Saves placement now and marks the window to reopen next time (Windows is signing out).</summary>
    public void RememberForExit()
    {
        Remember();
        _state.Open = true;
    }

    private void Remember()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            _state.HasPosition = true;
            _state.Left = bounds.Left;
            _state.Top = bounds.Top;
            _state.Width = bounds.Width;
            _state.Height = bounds.Height;
        }

        _state.Maximized = WindowState == WindowState.Maximized;
        _state.Topmost = Topmost;
        _state.Opacity = Opacity;
    }

    private void SetTopmost(bool value)
    {
        Topmost = value;
        TopmostItem.IsChecked = value;
        Remember();
        _save();
    }

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Background_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleFullScreen();
        }
        else if (WindowState == WindowState.Normal && e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Topmost_Click(object sender, RoutedEventArgs e) => SetTopmost(TopmostItem.IsChecked);

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
        {
            Opacity = opacity;
            Remember();
            _save();
        }
    }

    /// <summary>Size submenu: fit to height or a manual size (kept with this window's placement).</summary>
    private void SetContentSize(bool fitToHeight, double scale)
    {
        _state.FitToHeight = fitToHeight;
        _state.ContentScale = ContentSize.Clamp(scale);
        DashCanvas.SetContentSize(_state.FitToHeight, _state.ContentScale);
        _save();
    }

    private void FullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
