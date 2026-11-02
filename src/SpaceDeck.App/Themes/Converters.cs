using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using SpaceDeck.App.Services;
using SpaceDeck.Core.Model;

namespace SpaceDeck.App.Themes;

/// <summary>RailStatus or ValueLevel → skin status brush (Ok/Warning/Critical/Unknown).</summary>
public sealed class StatusToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RailStatus.Ok => SkinManager.Brush("Skin.Ok"),
        RailStatus.Warning or ValueLevel.Warning => SkinManager.Brush("Skin.Warning"),
        RailStatus.Critical or ValueLevel.Critical => SkinManager.Brush("Skin.Critical"),
        ValueLevel.Normal => SkinManager.Brush("Skin.Text"),
        SpaceDeck.Core.Alerts.AlertLevel.Warning => SkinManager.Brush("Skin.Warning"),
        SpaceDeck.Core.Alerts.AlertLevel.Critical => SkinManager.Brush("Skin.Critical"),
        SpaceDeck.Core.Alerts.AlertLevel.Normal => SkinManager.Brush("Skin.Ok"),
        SpaceDeck.App.ViewModels.DiagnosticState.Ok => SkinManager.Brush("Skin.Ok"),
        SpaceDeck.App.ViewModels.DiagnosticState.Info => SkinManager.Brush("Skin.TextMuted"),
        SpaceDeck.App.ViewModels.DiagnosticState.Warning => SkinManager.Brush("Skin.Warning"),
        SpaceDeck.App.ViewModels.DiagnosticState.Error => SkinManager.Brush("Skin.Critical"),
        _ => SkinManager.Brush("Skin.Unknown"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Series color index (int) → the skin's Skin.Series.N brush; null → the skin's ink.</summary>
public sealed class SeriesBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is int i ? SkinManager.Brush($"Skin.Series.{((i % 6) + 6) % 6}") : SkinManager.Brush("Skin.Text");

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>true ↔ false.</summary>
public sealed class NotConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

public sealed class ConnectionStateToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        ConnectionState.Connected => SkinManager.Brush("Skin.Connected"),
        ConnectionState.Connecting => SkinManager.Brush("Skin.Connecting"),
        _ => SkinManager.Brush("Skin.Disconnected"),
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Collapsed for null, empty strings, false, zero and RailStatus.Unknown; visible otherwise. Parameter "invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value switch
        {
            null => false,
            bool b => b,
            string s => s.Length > 0,
            RailStatus r => r != RailStatus.Unknown,
            int i => i > 0,
            _ => true,
        };

        if (parameter is string p && p.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Nullable float → double for numeric dependency properties; null becomes NaN (drawn as "no data").</summary>
public sealed class ToDoubleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        float f => (double)f,
        double d => d,
        int i => (double)i,
        _ => double.NaN,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Binding converter for times shown in XAML (follows the clock-format setting).</summary>
public sealed class TimeTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime time ? SpaceDeck.App.Services.Appearance.Time(time, seconds: parameter as string == "seconds") : parameter is null ? null : "no data yet";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
