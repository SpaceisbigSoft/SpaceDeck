using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SpaceDeck.App.ViewModels;

namespace SpaceDeck.App.Views;

/// <summary>
/// Right-click items for a dashboard tile, shared by the dashboard page, dashboard windows and the sidebar: hide the
/// clicked section row, show hidden rows again, edit the tile.
/// </summary>
public static class SectionRowMenu
{
    /// <summary>The tile (and section row, if any) under a right-clicked element.</summary>
    public static (TileViewModel? Tile, SectionRow? Row) Find(object? originalSource)
    {
        TileViewModel? tile = null;
        SectionRow? row = null;
        for (var element = originalSource as DependencyObject; element is not null;
             element = element is Visual ? VisualTreeHelper.GetParent(element) : LogicalTreeHelper.GetParent(element))
        {
            if (element is FrameworkElement fe)
            {
                row ??= fe.DataContext as SectionRow;
                if (fe.DataContext is TileViewModel t)
                {
                    tile = t;
                    break;
                }
            }
        }

        return (tile, row);
    }

    /// <summary>
    /// Text for a menu header with its underscores doubled: sensor, row and dashboard names are user or hardware text,
    /// and a single "_" would otherwise become an access key and disappear ("VIN_3" showing as "VIN3").
    /// </summary>
    public static string MenuText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("_", "__", StringComparison.Ordinal);
    }

    /// <summary>Menu items for the tile/row; empty when nothing useful applies.</summary>
    public static IReadOnlyList<Control> Items(DashboardDocumentViewModel dashboard, TileViewModel tile, SectionRow? row)
    {
        ArgumentNullException.ThrowIfNull(dashboard);
        ArgumentNullException.ThrowIfNull(tile);
        var items = new List<Control>();
        if (tile.Type == Core.Dashboards.WidgetType.Section || tile.HasTitle)
        {
            var rename = new MenuItem { Header = $"Rename heading \u201C{MenuText(tile.Title)}\u201D…" };
            rename.Click += (_, _) => dashboard.RenameTitle(tile);
            items.Add(rename);
        }

        if (row is not null)
        {
            var hide = new MenuItem { Header = $"Hide “{MenuText(row.Label)}”" };
            hide.Click += (_, _) => dashboard.SetRowHidden(tile, row.Key, hidden: true);
            items.Add(hide);
        }

        if (tile.Spec.HiddenRows is { Count: > 0 } hidden)
        {
            var show = new MenuItem { Header = "Show hidden row" };
            foreach (var key in hidden.ToList())
            {
                var label = tile.Spec.Labels?.GetValueOrDefault(key) is { Length: > 0 } custom ? custom : key;
                var item = new MenuItem { Header = MenuText(label) };
                item.Click += (_, _) => dashboard.SetRowHidden(tile, key, hidden: false);
                show.Items.Add(item);
            }

            if (hidden.Count > 1)
            {
                show.Items.Add(new Separator());
                var all = new MenuItem { Header = "Show all" };
                all.Click += (_, _) =>
                {
                    foreach (var key in hidden.ToList())
                    {
                        dashboard.SetRowHidden(tile, key, hidden: false);
                    }
                };
                show.Items.Add(all);
            }

            items.Add(show);
        }

        var edit = new MenuItem { Header = tile.Type == Core.Dashboards.WidgetType.Section ? "Edit this section…" : "Edit this tile…" };
        edit.Click += (_, _) => dashboard.EditTileCommand.Execute(tile);
        items.Add(edit);
        return items;
    }
}
