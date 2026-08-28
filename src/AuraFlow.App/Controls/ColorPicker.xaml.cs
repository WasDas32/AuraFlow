using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using AuraFlow.App.Models;

namespace AuraFlow.App.Controls;

/// <summary>Compact HSV color picker with hex input and preset swatches.</summary>
public partial class ColorPicker : UserControl
{
    private double _h = 210; // 0..360
    private double _s = 1;   // 0..1
    private double _v = 1;   // 0..1
    private bool _updating;

    public static readonly RoutedEvent ColorChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(ColorChanged), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(ColorPicker));

    public event RoutedEventHandler ColorChanged
    {
        add => AddHandler(ColorChangedEvent, value);
        remove => RemoveHandler(ColorChangedEvent, value);
    }

    public SerializableColor CurrentColor
    {
        get
        {
            ColorMath.HsvToRgb(_h / 360.0, _s, _v, out byte r, out byte g, out byte b);
            return new SerializableColor(r, g, b);
        }
    }

    public ColorPicker()
    {
        InitializeComponent();
        BuildPresets();
        SetColor(new SerializableColor(0, 120, 255));
    }

    public void SetColor(SerializableColor color)
    {
        _updating = true;
        RgbToHsv(color.R, color.G, color.B, out _h, out _s, out _v);
        HueSlider.Value = _h;
        UpdateSvThumb();
        RefreshVisuals();
        _updating = false;
    }

    private static void RgbToHsv(byte r, byte g, byte b, out double h, out double s, out double v)
    {
        var m = System.Windows.Media.Color.FromRgb(r, g, b);
        m.GetHsv(out h, out s, out v);
    }

    private void BuildPresets()
    {
        var presets = new[]
        {
            "#FFFFFF", "#B0BEC5", "#546E7A", "#1B1B1B",
            "#FF3B30", "#FF9500", "#FFCC00", "#34C759",
            "#00C7BE", "#30B0C7", "#007AFF", "#5856D6",
            "#AF52DE", "#FF2D55", "#8E44AD", "#16A085",
            "#C0392B", "#2C3E50",
        };
        foreach (var hex in presets)
        {
            if (!SerializableColor.TryParseHex(hex, out var c))
            {
                continue;
            }

            var btn = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 7, 7),
                Background = new SolidColorBrush(c.ToMedia()),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
                Tag = c,
                Template = MakeSwatchTemplate(),
            };
            btn.Click += (_, _) =>
            {
                SetColor((SerializableColor)btn.Tag);
                RaiseColorChanged();
            };
            PresetsList.Items.Add(btn);
        }
    }

    private ControlTemplate MakeSwatchTemplate()
    {
        var xaml = "<ControlTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" TargetType=\"Button\">" +
                   "<Border Background=\"{TemplateBinding Background}\" BorderBrush=\"{TemplateBinding BorderBrush}\" " +
                   "BorderThickness=\"{TemplateBinding BorderThickness}\" CornerRadius=\"7\"/>" +
                   "</ControlTemplate>";
        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(xaml);
    }

    private void RefreshVisuals()
    {
        ColorMath.HsvToRgb(_h / 360.0, 1, 1, out byte hr, out byte hg, out byte hb);
        var hueColor = Color.FromRgb(hr, hg, hb);
        HueBackdrop.Fill = new SolidColorBrush(hueColor);

        var cur = CurrentColor;
        CurrentSwatch.Background = new SolidColorBrush(cur.ToMedia());
        HexBox.Text = cur.ToHex();
        UpdateSvThumb();
    }

    private void UpdateSvThumb()
    {
        double w = SvBox.ActualWidth > 0 ? SvBox.ActualWidth : 236;
        double h = SvBox.ActualHeight > 0 ? SvBox.ActualHeight : 150;
        Canvas.SetLeft(SvThumb, Math.Clamp((_s * w) - 6.5, -2, w - 11));
        Canvas.SetTop(SvThumb, Math.Clamp(((1 - _v) * h) - 6.5, -2, h - 11));

        ColorMath.HsvToRgb(_h / 360.0, _s, _v, out byte r, out byte g, out byte b);
        SvThumb.Stroke = new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    private void Sv_MouseDown(object sender, MouseButtonEventArgs e)
    {
        SvCapture.CaptureMouse();
        ApplySvFromMouse(e.GetPosition(SvBox));
    }

    private void Sv_MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            ApplySvFromMouse(e.GetPosition(SvBox));
        }
    }

    private void Sv_MouseUp(object sender, MouseButtonEventArgs e) => SvCapture.ReleaseMouseCapture();

    private void ApplySvFromMouse(Point p)
    {
        double w = SvBox.ActualWidth;
        double h = SvBox.ActualHeight;
        _s = Math.Clamp(p.X / w, 0, 1);
        _v = Math.Clamp(1 - (p.Y / h), 0, 1);
        UpdateSvThumb();
        RefreshVisuals();
        RaiseColorChanged();
    }

    private void Hue_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        _h = e.NewValue;
        RefreshVisuals();
        RaiseColorChanged();
    }

    private void HexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyHex();
        }
    }

    private void HexBox_LostFocus(object sender, RoutedEventArgs e) => ApplyHex();

    private void ApplyHex()
    {
        if (SerializableColor.TryParseHex(HexBox.Text, out var c))
        {
            SetColor(c);
            RaiseColorChanged();
        }
        else
        {
            HexBox.Text = CurrentColor.ToHex();
        }
    }

    private void RaiseColorChanged() => RaiseEvent(new RoutedEventArgs(ColorChangedEvent));
}

internal static class HsvExtensions
{
    /// <summary>System.Windows.Media.Color lacks GetHsv - provide it.</summary>
    public static void GetHsv(this Color c, out double h, out double s, out double v)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        v = max;
        s = max <= 0 ? 0 : delta / max;

        if (delta <= 0)
        {
            h = 0;
            return;
        }

        if (max == r)
        {
            h = 60 * (((g - b) / delta) % 6);
        }
        else if (max == g)
        {
            h = 60 * (((b - r) / delta) + 2);
        }
        else
        {
            h = 60 * (((r - g) / delta) + 4);
        }

        if (h < 0)
        {
            h += 360;
        }
    }
}
