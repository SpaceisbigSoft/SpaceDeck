using System.Globalization;
using System.Windows.Controls;

namespace SpaceDeck.App.Services;

/// <summary>
/// The per-window content size of the sidebar and dashboard windows. "Fit to height" shrinks the content until all of it
/// fits the window height (never below <see cref="Minimum"/>, never above 100 %); otherwise a manual size of
/// 70–150 % in 5 % steps. Applied by <see cref="SpaceDeck.App.Views.DashboardCanvas.SetContentSize"/>, on top of the global UI size
/// (<see cref="Appearance.Scale"/>). The LAN web dashboard ignores it.
/// </summary>
public static class ContentSize
{
    /// <summary>Smallest manual size, and the floor for fit to height (below it the content scrolls and a hint shows).</summary>
    public const double Minimum = 0.7;

    /// <summary>Largest manual size.</summary>
    public const double Maximum = 1.5;

    /// <summary>The steps "Smaller" / "Larger" take, and the values Settings offers.</summary>
    public const double Step = 0.05;

    /// <summary>A manual size rounded to the nearest step inside 70–150 %; 100 % for a hand-edited NaN/infinity.</summary>
    public static double Clamp(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(Math.Round(scale / Step) * Step, Minimum, Maximum) : 1;

    /// <summary>The next step down (<paramref name="direction"/> &lt; 0) or up from any size (a fitted 87 % → 85 % or 90 %).</summary>
    public static double Next(double current, int direction)
    {
        var steps = (double.IsFinite(current) ? current : 1) / Step;
        var next = direction < 0 ? Math.Ceiling(steps - 1e-6) - 1 : Math.Floor(steps + 1e-6) + 1;
        return Clamp(next * Step);
    }

    /// <summary>"85 %".</summary>
    public static string Percent(double scale) =>
        Math.Round(scale * 100).ToString("0", CultureInfo.CurrentCulture) + " %";

    /// <summary>
    /// Fills a window's "Size" submenu: the current size, "Fit to height" (checkable), "Smaller", "Larger" and "100 %".
    /// Choosing a manual size turns fit off. <paramref name="apply"/> receives (fit to height, manual size).
    /// </summary>
    /// <param name="fitToHeight">Whether fit to height is on now.</param>
    /// <param name="effective">The size shown now (the fitted one while fitting).</param>
    public static void FillMenu(MenuItem menu, Func<bool> fitToHeight, Func<double> effective, Action<bool, double> apply)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(fitToHeight);
        ArgumentNullException.ThrowIfNull(effective);
        ArgumentNullException.ThrowIfNull(apply);

        var now = new MenuItem { IsEnabled = false };
        var fit = new MenuItem { Header = "Fit to height", IsCheckable = true };
        var smaller = new MenuItem { Header = "Smaller" };
        var larger = new MenuItem { Header = "Larger" };
        var actual = new MenuItem { Header = "100 %" };

        // Turning fit off keeps roughly the size it had fitted to.
        fit.Click += (_, _) => apply(fit.IsChecked, Clamp(effective()));
        smaller.Click += (_, _) => apply(false, Next(effective(), -1));
        larger.Click += (_, _) => apply(false, Next(effective(), 1));
        actual.Click += (_, _) => apply(false, 1);

        menu.Items.Clear();
        menu.Items.Add(now);
        menu.Items.Add(new Separator());
        menu.Items.Add(fit);
        menu.Items.Add(smaller);
        menu.Items.Add(larger);
        menu.Items.Add(actual);

        void Refresh()
        {
            var scale = effective();
            now.Header = "Now " + Percent(scale);
            fit.IsChecked = fitToHeight();
            smaller.IsEnabled = scale > Minimum + 0.001;
            larger.IsEnabled = scale < Maximum - 0.001;
        }

        menu.SubmenuOpened += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, menu))
            {
                Refresh();
            }
        };
        Refresh();
    }
}
