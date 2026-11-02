using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace SpaceDeck.App.Services;

/// <summary>App-wide look settings that aren't part of a skin: text/layout size and the clock format.</summary>
public static class Appearance
{
    private static double _scale = 1;

    /// <summary>Raised after <see cref="Scale"/> or <see cref="ClockFormat"/> changes.</summary>
    public static event Action? Changed;

    public static double Scale => _scale;

    /// <summary>"system", "12" or "24".</summary>
    public static string ClockFormat { get; private set; } = "system";

    public static void Set(double scale, string clockFormat)
    {
        _scale = Math.Clamp(double.IsFinite(scale) ? scale : 1, 0.8, 1.6);
        ClockFormat = clockFormat is "12" or "24" ? clockFormat : "system";
        Changed?.Invoke();
    }

    /// <summary>Scales a window's content (call once per window; follows later changes).</summary>
    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var minWidth = window.MinWidth;
        var minHeight = window.MinHeight;
        void Apply()
        {
            if (window.Content is FrameworkElement root)
            {
                root.LayoutTransform = Math.Abs(_scale - 1) < 0.001 ? Transform.Identity : new ScaleTransform(_scale, _scale);
            }

            // Scaled content needs a scaled minimum, or pages get squeezed at 150 %.
            window.MinWidth = minWidth * _scale;
            window.MinHeight = minHeight * _scale;
        }

        Changed += Apply;
        window.Closed += (_, _) => Changed -= Apply;
        Apply();
    }

    /// <summary>"#C2410C" → color; false for null/invalid.</summary>
    public static bool TryParseColor(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        try
        {
            color = (Color)ColorConverter.ConvertFromString(text.Trim());
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or NullReferenceException)
        {
            return false;
        }
    }

    /// <summary>"14:05", "2:05 PM" or the system's short time; with seconds when asked.</summary>
    public static string Time(DateTime time, bool seconds = false) => ClockFormat switch
    {
        "24" => time.ToString(seconds ? "HH:mm:ss" : "HH:mm", CultureInfo.CurrentCulture),
        "12" => time.ToString(seconds ? "h:mm:ss tt" : "h:mm tt", CultureInfo.CurrentCulture),
        _ => time.ToString(seconds ? "T" : "t", CultureInfo.CurrentCulture),
    };
}
