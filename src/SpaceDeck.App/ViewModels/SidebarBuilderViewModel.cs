using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Dashboards;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.ViewModels;

/// <summary>A row checkbox under a section in the sidebar builder (or the tile editor).</summary>
public sealed partial class SectionOptionChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public SectionOptionChoice(SectionOption option, bool isChecked)
    {
        ArgumentNullException.ThrowIfNull(option);
        Key = option.Key;
        Label = option.Label;
        IsChecked = isChecked;
    }

    public string Key { get; }

    public string Label { get; }

    public static ObservableCollection<SectionOptionChoice> For(SectionKind kind, IReadOnlyCollection<string>? on)
    {
        ArgumentNullException.ThrowIfNull(kind);
        var chosen = on ?? kind.Defaults;
        return [.. kind.Options.Select(o => new SectionOptionChoice(o, chosen.Contains(o.Key)))];
    }
}

/// <summary>A section checkbox in the sidebar builder, with its row options.</summary>
public sealed partial class SectionChoice : ObservableObject
{
    [ObservableProperty]
    private bool _isChecked;

    public SectionChoice(SectionCandidate candidate, TileSpec? existing = null, bool editing = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        Candidate = candidate;
        Existing = existing;
        IsChecked = editing ? existing is not null : candidate.Recommended;
        Options = SectionOptionChoice.For(candidate.Kind, existing?.Rows);
    }

    /// <summary>The section tile this choice came from when updating a sidebar (keeps its title and row names).</summary>
    public TileSpec? Existing { get; }

    public SectionCandidate Candidate { get; }

    public string Label => Candidate.Label;

    public string Glyph => Candidate.Kind.Glyph;

    public ObservableCollection<SectionOptionChoice> Options { get; }
}

/// <summary>
/// "Build sidebar": tick the sections (CPU, GPU, RAM, drives, network…) and their rows; makes a narrow dashboard
/// with the sections stacked, each tall enough for its rows.
/// </summary>
public sealed partial class SidebarBuilderViewModel : ObservableObject
{
    private readonly SensorStore _store;

    [ObservableProperty]
    private bool _includeClock = true;

    [ObservableProperty]
    private string _name = "Sidebar";

    [ObservableProperty]
    private Option<int> _width = null!;

    [ObservableProperty]
    private bool _saveAsNew;

    [ObservableProperty]
    private Option<string> _fanMode = FanDisplay.Choices[0];

    public IReadOnlyList<Option<string>> FanModes => FanDisplay.Choices;

    /// <param name="existing">The sidebar dashboard to update (its sections come pre-ticked), or null to make a new one.</param>
    public SidebarBuilderViewModel(SensorStore store, int columns, DashboardDocument? existing = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        _store = store;
        IsUpdate = existing is not null;
        var sections = existing?.Tiles.Where(t => t.Type == WidgetType.Section).ToList() ?? new List<TileSpec>();
        var used = new HashSet<TileSpec>();
        var choices = new List<SectionChoice>();
        foreach (var candidate in Sections.Available(store.Sensors))
        {
            // Same section kind and hardware; a section without a chosen hardware is the first one of its kind.
            var match = sections.FirstOrDefault(t => !used.Contains(t) && t.Section == candidate.Kind.Key
                && (t.Hardware == candidate.HardwareId || (t.Hardware is null && choices.All(c => c.Candidate.Kind != candidate.Kind))));
            if (match is not null)
            {
                used.Add(match);
            }

            choices.Add(new SectionChoice(candidate, match, IsUpdate));
        }

        Choices = [.. choices];
        if (existing is not null)
        {
            Name = existing.Name;
            IncludeClock = existing.Tiles.Any(t => t.Type == WidgetType.Clock);
            var savedMode = sections.FirstOrDefault(t => t.Section is "gpu" or "motherboard")?.Metric;
            FanMode = FanDisplay.Choices.FirstOrDefault(c => c.Value == savedMode) ?? FanDisplay.Choices[0];
            columns = sections.Select(t => t.ColSpan).DefaultIfEmpty(columns).Max();
        }

        Width = Widths.FirstOrDefault(w => w.Value == columns) ?? Widths[0];
    }

    /// <summary>Updating the sidebar's current dashboard (rather than making a new one).</summary>
    public bool IsUpdate { get; }

    public string Heading => IsUpdate ? "Update the sidebar" : "Build a sidebar";

    public string ActionText => IsUpdate ? "Update" : "Create";

    /// <summary>Set by Create: replace the existing dashboard's layout instead of adding a new dashboard.</summary>
    public bool ReplaceExisting => IsUpdate && !SaveAsNew;

    public ObservableCollection<SectionChoice> Choices { get; }

    public bool HasNoSensors => _store.Sensors.Count == 0;

    public IReadOnlyList<Option<int>> Widths { get; } =
        [new("Normal (3 columns)", 3), new("Wide (4 columns)", 4), new("Extra wide (6 columns)", 6)];

    /// <summary>Set when Create was pressed.</summary>
    public DashboardDocument? Result { get; private set; }

    public event Action? RequestClose;

    [RelayCommand]
    private void Create()
    {
        var columns = Width.Value;
        var doc = new DashboardDocument { Name = string.IsNullOrWhiteSpace(Name) ? "Sidebar" : Name.Trim() };
        var row = 0;
        if (IncludeClock)
        {
            doc.Tiles.Add(new TileSpec { Type = WidgetType.Clock, Col = 0, Row = row, ColSpan = columns, RowSpan = 2 });
            row += 2;
        }

        foreach (var choice in Choices.Where(c => c.IsChecked))
        {
            var spec = new TileSpec
            {
                Type = WidgetType.Section,
                Section = choice.Candidate.Kind.Key,
                Hardware = choice.Candidate.HardwareId,
                Rows = [.. choice.Options.Where(o => o.IsChecked).Select(o => o.Key)],
                Title = choice.Existing?.Title,
                Metric = FanMode.Value,
                Labels = choice.Existing?.Labels is { } labels ? new Dictionary<string, string>(labels, StringComparer.Ordinal) : null,
                HiddenRows = choice.Existing?.HiddenRows?.ToList(),
                Col = 0,
                Row = row,
                ColSpan = columns,
            };
            spec.RowSpan = Sections.FitRowSpan(Sections.Plan(spec, _store.Sensors), showTitle: true);
            doc.Tiles.Add(spec);
            row += spec.RowSpan;
        }

        Result = doc;
        RequestClose?.Invoke();
    }
}
