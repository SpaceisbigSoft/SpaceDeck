using System.Windows;
using Forms = System.Windows.Forms;

namespace SpaceDeck.App.Services;

/// <summary>
/// The tray icon: Windows notifications (alerts), the live hover readout (<see cref="TrayReadout"/>) and a small menu
/// (open, fan profile, click-through, sidebar, exit).
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;

    /// <summary>The icon extracted from the exe (a GDI handle we own); null when the shared stock icon is used.</summary>
    private readonly System.Drawing.Icon? _ownedIcon;

    public TrayService()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open SpaceDeck", null, (_, _) => ShowMainWindow(false));
        var fanProfile = new Forms.ToolStripMenuItem("Fan profile") { Visible = false };
        // A dummy child so the item shows its submenu arrow before the first fill.
        fanProfile.DropDownItems.Add(new Forms.ToolStripMenuItem("…") { Enabled = false });
        menu.Items.Add(fanProfile);
        var clickThrough = new Forms.ToolStripMenuItem("Click-through dashboard windows") { CheckOnClick = false };
        clickThrough.Click += (_, _) => ClickThroughToggled?.Invoke();
        menu.Items.Add(clickThrough);
        var sidebar = new Forms.ToolStripMenuItem("Show sidebar") { CheckOnClick = false };
        sidebar.Click += (_, _) => SidebarToggled?.Invoke();
        menu.Items.Add(sidebar);
        menu.Opening += (_, _) =>
        {
            clickThrough.Checked = IsClickThrough?.Invoke() == true;
            sidebar.Checked = IsSidebarOn?.Invoke() == true;
            FillFanProfiles(fanProfile);
        };
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) =>
        {
            AppLifetime.IsExiting = true;
            Application.Current?.Shutdown();
        });

        _ownedIcon = LoadIcon();
        _icon = new Forms.NotifyIcon
        {
            Text = "SpaceDeck",
            Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => ShowMainWindow(false);
        _icon.BalloonTipClicked += (_, _) => ShowMainWindow(true);
    }

    /// <summary>The tray menu's "Click-through dashboard windows" was clicked.</summary>
    public event Action? ClickThroughToggled;

    /// <summary>Tells the tray menu whether click-through is on (for its check mark).</summary>
    public Func<bool>? IsClickThrough { get; set; }

    /// <summary>The "Show sidebar" menu item was clicked.</summary>
    public event Action? SidebarToggled;

    /// <summary>Whether the sidebar is on (for the check mark).</summary>
    public Func<bool>? IsSidebarOn { get; set; }

    /// <summary>
    /// Lists FanControl's saved profiles and the active one for the "Fan profile" submenu (null or an empty list hides
    /// it, e.g. FanControl not found or a replay window).
    /// </summary>
    public Func<(IReadOnlyList<string> Profiles, string? Active)>? GetFanProfiles { get; set; }

    /// <summary>A profile was picked in the tray's "Fan profile" submenu.</summary>
    public event Action<string>? FanProfilePicked;

    /// <summary>Raised when the user clicks a notification (after the window is shown).</summary>
    public event Action? NotificationClicked;

    /// <summary>Shows a Windows notification.</summary>
    public void Notify(string title, string text, bool critical)
    {
        // Balloon text is limited to 255 characters, the title to 63.
        _icon.ShowBalloonTip(
            8000,
            Truncate(title, 63),
            Truncate(text, 255),
            critical ? Forms.ToolTipIcon.Error : Forms.ToolTipIcon.Warning);
    }

    /// <summary>Hover text: a short live readout (Windows allows 127 characters).</summary>
    public void SetTooltip(string text) => _icon.Text = Truncate(text, 127);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _ownedIcon?.Dispose();
    }

    private void ShowMainWindow(bool fromNotification)
    {
        if (Application.Current?.MainWindow is not { } window)
        {
            return;
        }

        if (window is MainWindow main)
        {
            main.RestoreFromTray();
        }
        else
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Show();
            window.Activate();
        }
        if (fromNotification)
        {
            NotificationClicked?.Invoke();
        }
    }

    /// <summary>Rebuilds the "Fan profile" submenu each time the tray menu opens (FanControl's list can change).</summary>
    private void FillFanProfiles(Forms.ToolStripMenuItem parent)
    {
        var data = GetFanProfiles?.Invoke();
        IReadOnlyList<string> profiles = data?.Profiles ?? [];
        var active = data?.Active;
        parent.Visible = profiles.Count > 0;
        if (profiles.Count == 0)
        {
            return;
        }

        foreach (var old in parent.DropDownItems.Cast<Forms.ToolStripItem>().ToList())
        {
            parent.DropDownItems.Remove(old);
            old.Dispose();
        }

        foreach (var name in profiles)
        {
            // "&" in a name would otherwise turn the next letter into an access key.
            var item = new Forms.ToolStripMenuItem(name.Replace("&", "&&", StringComparison.Ordinal))
            {
                Checked = string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
            };
            var picked = name;
            item.Click += (_, _) => FanProfilePicked?.Invoke(picked);
            parent.DropDownItems.Add(item);
        }

        parent.Text = active is null ? "Fan profile" : $"Fan profile: {active.Replace("&", "&&", StringComparison.Ordinal)}";
    }

    /// <summary>
    /// The exe's own icon (Assets\SpaceDeck.ico) at the tray's size, so Windows uses the hand-tuned small image instead of
    /// shrinking the large one; null makes the caller fall back to the stock icon.
    /// </summary>
    private static System.Drawing.Icon? LoadIcon()
    {
        try
        {
            if (Environment.ProcessPath is not { } exe)
            {
                return null;
            }

            var size = Forms.SystemInformation.SmallIconSize.Width;
            return System.Drawing.Icon.ExtractIcon(exe, 0, size) ?? System.Drawing.Icon.ExtractAssociatedIcon(exe);
        }
        catch (Exception ex) when (ex is ArgumentException or System.IO.IOException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
