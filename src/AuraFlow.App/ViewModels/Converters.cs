using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using AuraFlow.App.Models;

namespace AuraFlow.App.ViewModels;

/// <summary>true -> Visible, false -> Collapsed.</summary>
public sealed class BoolToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>false -> Visible, true -> Collapsed.</summary>
public sealed class InverseBoolToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

public sealed class BoolToVisInverse : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is false ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>
/// Shows an element only when the bound Layer.Type matches one of the comma-separated
/// effect type names in ConverterParameter. Example: Parameter="RainbowCycle,RainbowWave".
/// </summary>
public sealed class EffectTypeToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not EffectType type || p is not string names)
        {
            return Visibility.Collapsed;
        }

        foreach (var part in names.Split('|'))
        {
            if (Enum.TryParse(part.Trim(), ignoreCase: true, out EffectType parsed) && parsed == type)
            {
                return Visibility.Visible;
            }
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>null -> Collapsed, anything else -> Visible.</summary>
public sealed class NullToVis : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is null ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

/// <summary>bool -> colored status brush (green/red).</summary>
public sealed class StatusBrush : IValueConverter
{
    private static readonly SolidColorBrush Ok = new(Color.FromRgb(0x46, 0xC4, 0x6F));
    private static readonly SolidColorBrush Bad = new(Color.FromRgb(0xE5, 0x48, 0x4D));

    static StatusBrush()
    {
        Ok.Freeze();
        Bad.Freeze();
    }

    public object Convert(object value, Type t, object p, CultureInfo c)
        => value is true ? Ok : Bad;

    public object ConvertBack(object value, Type t, object p, CultureInfo c)
        => throw new NotSupportedException();
}

