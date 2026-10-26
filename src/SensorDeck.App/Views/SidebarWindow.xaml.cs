using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using SensorDeck.App.Services;
using SensorDeck.App.Themes;
using SensorDeck.App.ViewModels;
using SensorDeck.Core.Dashboards;

namespace SensorDeck.App.Views;

/// <summary>
/// A dashboard docked to the left or right edge of a chosen monitor, full height. With "reserve screen space" it is a
/// Windows app bar (like the taskbar), so other windows and maximizing stay clear of it; otherwise it floats on top.
/// It hides while a fullscreen game or video covers its monitor (optional). All placement is in physical pixels.
/// </summary>
public partial class SidebarWindow : Window
{
    /// <summary>Space the canvas keeps on the right for its (overlay) scrollbar, in DIPs.</summary>
    private const double ScrollbarRoom = 16;

    private const int WmDisplayChange = 0x007E;
    private const int WmSettingChange = 0x001A;
    private const int WmDpiChanged = 0x02E0;
    private const int SpiSetWorkArea = 0x002F;

    private readonly SidebarSettings _settings;
    private readonly Action _save;
    private readonly Func<IReadOnlyList<DashboardDocumentViewModel>> _dashboards;
    private readonly Action _turnOff;
    private readonly DispatcherTimer _dockTimer;
    private IntPtr _hwnd;
    private AppBar? _appBar;
    private bool _reserved;
    private bool _hiddenForFullscreen;
    private PixelRect _monitorBounds;
    private TileViewModel? _menuTile;
    private SectionRow? _menuRow;
    private readonly List<Control> _tileItems = [];

    public SidebarWindow(SidebarSettings settings, Action save, Func<IReadOnlyList<DashboardDocumentViewModel>> dashboards, Action turnOff)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _save = save;
        _dashboards = dashboards;
        _turnOff = turnOff;
        InitializeComponent();
        Appearance.Attach(this);
        GlassEffect.Attach(this);
        Opacity = ClampOpacity(settings.Opacity);
        DashCanvas.SetContentSize(settings.FitToHeight, settings.ContentScale);
        ContentSize.FillMenu(SizeMenu, () => _settings.FitToHeight, () => DashCanvas.EffectiveScale, (fit, scale) => Change(() =>
        {
            _settings.FitToHeight = fit;
            _settings.ContentScale = ContentSize.Clamp(scale);
        }));

        // Display and taskbar changes come in bursts; dock once things settle.
        _dockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _dockTimer.Tick += (_, _) =>
        {
            _dockTimer.Stop();
            Dock();
        };

        SourceInitialized += OnSourceInitialized;

        // Remember which tile (and section row) was right-clicked, for "Hide …" and "Edit this section…".
        ContextMenuOpening += (_, e) => (_menuTile, _menuRow) = SectionRowMenu.Find(e.OriginalSource);
        Appearance.Changed += ScheduleDock;
        Closed += (_, _) =>
        {
            Appearance.Changed -= ScheduleDock;
            _dockTimer.Stop();
            _appBar?.Dispose(); // gives the screen space back
            _appBar = null;
        };
    }

    public void ShowDashboard(DashboardDocumentViewModel? dashboard) => DataContext = dashboard;

    /// <summary>Places the window on its monitor edge (and reserves the space when asked). Safe to call any time.</summary>
    public void Dock()
    {
        if (_appBar is null || _hwnd == IntPtr.Zero)
        {
            return;
        }

        var monitor = Monitors.Find(_settings.Monitor);
        _monitorBounds = monitor.Bounds;
        var right = _settings.Edge == SidebarEdge.Right;
        var columns = Math.Clamp(_settings.Columns, SidebarSettings.MinColumns, SidebarSettings.MaxColumns); // the widths the menus offer; a hand-edited value can't fill the screen
        // The UI-size scale applies inside the canvas's margin (WPF subtracts the margin before the layout transform).
        var widthDip = (((columns * GridLayout.ColumnPitchPx) - GridLayout.GapPx + ScrollbarRoom) * Appearance.Scale) + DashCanvas.Margin.Left + 1;
        var widthPx = (int)Math.Ceiling(widthDip * Monitors.ScaleOf(monitor));

        PixelRect rect;
        if (_settings.ReserveSpace)
        {
            rect = _appBar.Reserve(monitor.Bounds, right, widthPx);
            _reserved = true;

            // The reservation spans the monitor; the window itself stays clear of a taskbar at the top or bottom.
            var work = Monitors.WorkAreaOf(monitor);
            rect = rect with { Top = Math.Max(rect.Top, work.Top), Bottom = Math.Min(rect.Bottom, work.Bottom) };
        }
        else
        {
            if (_reserved)
            {
                _appBar.Release();
                _reserved = false;
            }

            rect = AppBar.Strip(Monitors.WorkAreaOf(monitor), right, widthPx);
        }

        SetWindowPos(_hwnd, HwndTopmost, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoActivate);
    }

    private void ScheduleDock()
    {
        _dockTimer.Stop();
        _dockTimer.Start();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // A tool window: not in Alt+Tab or the taskbar.
        var style = GetWindowLongPtr(_hwnd, GwlExStyle).ToInt64();
        SetWindowLongPtr(_hwnd, GwlExStyle, new IntPtr((style | WsExToolWindow) & ~WsExAppWindow));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        _appBar = new AppBar(_hwnd);
        _appBar.Register();
        Dock();

        // A fullscreen game may already be running (no notification will come for it).
        if (_settings.HideWhenFullscreen && AppBar.IsFullscreenOn(_monitorBounds))
        {
            _hiddenForFullscreen = true;
            Dispatcher.BeginInvoke(new Action(Hide), DispatcherPriority.Background);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_appBar is not null && msg == _appBar.TaskbarCreatedMessage)
        {
            // Explorer restarted and forgot every app bar: register and dock again.
            _appBar.Reset();
            _reserved = false;
            _appBar.Register();
            ScheduleDock();
            return IntPtr.Zero;
        }

        if (_appBar is not null && msg == _appBar.CallbackMessage)
        {
            switch (wParam.ToInt32())
            {
                case AppBar.NotifyPosChanged when _settings.ReserveSpace:
                    ScheduleDock();
                    break;
                case AppBar.NotifyFullscreenApp:
                    OnFullscreenApp(lParam != IntPtr.Zero);
                    break;
            }

            handled = true;
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WmDpiChanged:
                // WPF resizes the window to Windows' suggestion after this; put it back right after that.
                Dispatcher.BeginInvoke(new Action(Dock), DispatcherPriority.Background);
                break;
            case WmDisplayChange:
                ScheduleDock(); // monitor plugged/unplugged, resolution or scaling changed
                break;
            case WmSettingChange when wParam.ToInt32() == SpiSetWorkArea && !_settings.ReserveSpace:
                ScheduleDock(); // the taskbar moved or resized (reserved bars hear about it as PosChanged)
                break;
        }

        return IntPtr.Zero;
    }

    private void OnFullscreenApp(bool opened)
    {
        if (opened)
        {
            if (_settings.HideWhenFullscreen && AppBar.IsFullscreenOn(_monitorBounds) && IsVisible)
            {
                _hiddenForFullscreen = true;
                Hide();
            }
        }
        else if (_hiddenForFullscreen)
        {
            _hiddenForFullscreen = false;
            Show();
            ScheduleDock();
        }
    }

    // ── Context menu ────────────────────────────────────────────────────

    private void Menu_Opened(object sender, RoutedEventArgs e)
    {
        // Tile items (hide row, show hidden rows, edit) go first, above the sidebar's own options.
        var menu = (ContextMenu)sender;
        foreach (var old in _tileItems)
        {
            menu.Items.Remove(old);
        }

        _tileItems.Clear();
        if (_menuTile is { } tile && DataContext is DashboardDocumentViewModel shown)
        {
            _tileItems.AddRange(SectionRowMenu.Items(shown, tile, _menuRow));
            _tileItems.Add(new Separator());
            for (var i = 0; i < _tileItems.Count; i++)
            {
                menu.Items.Insert(i, _tileItems[i]);
            }
        }

        DashboardsMenu.Items.Clear();
        foreach (var dashboard in _dashboards())
        {
            var item = new MenuItem { Header = SectionRowMenu.MenuText(dashboard.Name), IsCheckable = true, IsChecked = ReferenceEquals(DataContext, dashboard) };
            item.Click += (_, _) =>
            {
                _settings.DashboardId = dashboard.Id;
                _save();
                ShowDashboard(dashboard);
            };
            DashboardsMenu.Items.Add(item);
        }

        MonitorsMenu.Items.Clear();
        var current = Monitors.Find(_settings.Monitor).DeviceName;
        foreach (var monitor in Monitors.All())
        {
            var item = new MenuItem { Header = SectionRowMenu.MenuText(monitor.Label), IsCheckable = true, IsChecked = monitor.DeviceName == current };
            item.Click += (_, _) => Change(() => _settings.Monitor = monitor.DeviceName);
            MonitorsMenu.Items.Add(item);
        }

        LeftItem.IsChecked = _settings.Edge == SidebarEdge.Left;
        RightItem.IsChecked = _settings.Edge == SidebarEdge.Right;
        ReserveItem.IsChecked = _settings.ReserveSpace;
        FullscreenItem.IsChecked = _settings.HideWhenFullscreen;
    }

    /// <summary>Applies a settings change, saves and docks again.</summary>
    public void Change(Action change)
    {
        ArgumentNullException.ThrowIfNull(change);
        change();
        Opacity = ClampOpacity(_settings.Opacity);
        DashCanvas.SetContentSize(_settings.FitToHeight, _settings.ContentScale);
        if (_hiddenForFullscreen && !_settings.HideWhenFullscreen)
        {
            _hiddenForFullscreen = false;
            Show();
        }

        _save();
        Dock();
    }

    private void Left_Click(object sender, RoutedEventArgs e) => Change(() => _settings.Edge = SidebarEdge.Left);

    private void Right_Click(object sender, RoutedEventArgs e) => Change(() => _settings.Edge = SidebarEdge.Right);

    private void Reserve_Click(object sender, RoutedEventArgs e) => Change(() => _settings.ReserveSpace = ReserveItem.IsChecked);

    private void Fullscreen_Click(object sender, RoutedEventArgs e) => Change(() => _settings.HideWhenFullscreen = FullscreenItem.IsChecked);

    private void Width_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var columns))
        {
            Change(() => _settings.Columns = Math.Clamp(columns, SidebarSettings.MinColumns, SidebarSettings.MaxColumns));
        }
    }

    private void Opacity_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && double.TryParse(tag, NumberStyles.Float, CultureInfo.InvariantCulture, out var opacity))
        {
            Change(() => _settings.Opacity = ClampOpacity(opacity));
        }
    }

    /// <summary>Never fully see-through (the same 0.4–1 range AppSettings.Load enforces); also guards values set in code.</summary>
    private static double ClampOpacity(double opacity) => double.IsFinite(opacity) ? Math.Clamp(opacity, SidebarSettings.MinOpacity, 1) : 1;

    private void Hide_Click(object sender, RoutedEventArgs e) => _turnOff();

    // ── Win32 ───────────────────────────────────────────────────────────

    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x80;
    private const long WsExAppWindow = 0x40000;
    private const uint SwpNoActivate = 0x0010;
    private static readonly IntPtr HwndTopmost = new(-1);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);
}
