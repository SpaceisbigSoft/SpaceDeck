using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace SpaceDeck.App.Themes;

/// <summary>A look the user can pick in Settings.</summary>
/// <param name="Id">Saved in settings.</param>
public sealed record SkinChoice(string Id, string Name, string Description);

/// <summary>
/// Loads a skin (a ResourceDictionary of "Skin.*" keys) into the application resources and gives code access
/// to skin brushes. Swapping the dictionary restyles everything that uses DynamicResource, live. The skin's
/// "Skin.Base" (Light/Dark) also switches WPF's Fluent theme so stock controls match.
/// </summary>
public static class SkinManager
{
    public const string DefaultSkin = "Monochrome";
    public const string FluentSkin = "Fluent";

    private static ResourceDictionary? _current;
    private static string? _currentFile;
    private static bool _listeningForWindowsTheme;

    /// <summary>Raised after a skin is applied, so code that captured brushes can refresh.</summary>
    public static event Action? SkinChanged;

    /// <summary>Skins shipped with the app.</summary>
    public static IReadOnlyList<SkinChoice> Available { get; } =
    [
        new(DefaultSkin, "Monochrome", "Ink on paper, one accent (default)"),
        new("MonochromeDark", "Monochrome dark", "The same, on a dark ground"),
        new(FluentSkin, "Fluent", "Windows 11 cards, follows light/dark"),
        new("DarkHud", "Dark HUD", "Gaming HUD in cyan and lime"),
        new("Nord", "Nord", "Arctic blue-grey with frost accents"),
        new("Dracula", "Dracula", "Purple, pink and green on violet-grey"),
        new("Gruvbox", "Gruvbox", "Warm retro browns, orange and aqua"),
        new("Cyberpunk", "Cyberpunk", "Neon yellow, pink and cyan; hard edges"),
        new("Synthwave", "Synthwave", "80s sunset pink and cyan on purple"),
        new("RedTeam", "Red Team", "Black and graphite with racing red"),
        new("OledBlack", "OLED black", "Pure black, dimmed text, easy on OLED"),
        new("Paper", "Paper", "Warm off-white page with dark ink"),
        new("HighContrast", "High contrast", "White and yellow on black, bigger text"),
        new("AmberCrt", "Amber CRT", "Amber phosphor terminal, monospaced"),
        new("FrostedGlass", "Frosted glass", "Blurred see-through dashboards and sidebar"),
    ];

    /// <summary>The skin id currently applied (the user's choice, e.g. "Fluent", not "FluentDark").</summary>
    public static string Current { get; private set; } = DefaultSkin;

    /// <summary>The current skin's own accent (ignoring the user's override).</summary>
    public static Brush SkinAccent => _current?["Skin.Accent"] as Brush ?? Brushes.Gray;

    /// <summary>The user's accent color (null = the skin's own). Applied on top of every skin.</summary>
    public static Color? AccentOverride { get; private set; }

    /// <summary>Sets (or clears) the accent override and re-applies it live.</summary>
    public static void SetAccent(Color? color)
    {
        AccentOverride = color;
        ApplyAccent();
        SkinChanged?.Invoke();
    }

    private static void ApplyAccent()
    {
        var resources = Application.Current?.Resources;
        if (resources is null)
        {
            return;
        }

        // App-level keys win over the merged skin dictionary.
        resources.Remove("Skin.Accent");
        if (AccentOverride is { } color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            resources["Skin.Accent"] = brush;
        }
    }

    public static void Apply(string? id)
    {
        var skin = Available.Any(s => s.Id == id) ? id! : DefaultSkin;
        var file = skin == FluentSkin ? (WindowsUsesLightTheme() ? "FluentLight" : "FluentDark") : skin;
        if (file == _currentFile)
        {
            ApplyAccent();
            return;
        }

        var dictionary = new ResourceDictionary
        {
            Source = new Uri($"pack://application:,,,/SpaceDeck;component/Themes/Skins/{file}.xaml", UriKind.Absolute),
        };

        var app = Application.Current;

        // Switch WPF's own Fluent theme first (it manages its own dictionaries), then swap the skin in.
#pragma warning disable WPF0001 // Fluent ThemeMode is experimental
        app.ThemeMode = dictionary["Skin.Base"] as string == "Dark" ? ThemeMode.Dark : ThemeMode.Light;
#pragma warning restore WPF0001

        var merged = app.Resources.MergedDictionaries;
        if (_current is not null)
        {
            merged.Remove(_current);
        }

        // Insert first so Tokens.xaml (which references skin keys) can be merged after it.
        merged.Insert(0, dictionary);
        _current = dictionary;
        _currentFile = file;
        Current = skin;
        ApplyAccent();

        if (skin == FluentSkin && !_listeningForWindowsTheme)
        {
            // Follow Windows when the user switches light/dark while the app runs.
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
            _listeningForWindowsTheme = true;
        }

        SkinChanged?.Invoke();
    }

    /// <summary>A skin number (size, thickness) by key, or <paramref name="fallback"/>.</summary>
    public static double Double(string key, double fallback) =>
        Application.Current?.TryFindResource(key) is double d ? d : fallback;

    /// <summary>A skin brush by key, or a neutral fallback if the skin doesn't define it.</summary>
    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || Current != FluentSkin)
        {
            return;
        }

        // Raised on a system thread; skins must be applied on the UI thread.
        Application.Current?.Dispatcher.InvokeAsync(() =>
        {
            if (Current == FluentSkin)
            {
                Apply(FluentSkin);
            }
        });
    }

    /// <summary>Windows "app mode" (Settings → Personalization → Colors). Light if it can't be read.</summary>
    private static bool WindowsUsesLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is not int value || value != 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }
}
