using System.Globalization;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace AuraFlow.App.Models;

/// <summary>JSON-serializable RGB color (no alpha for storage simplicity).</summary>
public readonly struct SerializableColor : IEquatable<SerializableColor>
{
    public byte R { get; }
    public byte G { get; }
    public byte B { get; }

    [JsonConstructor]
    public SerializableColor(byte r, byte g, byte b)
    {
        R = r;
        G = g;
        B = b;
    }

    public static SerializableColor FromMedia(Color c) => new(c.R, c.G, c.B);

    public Color ToMedia() => Color.FromRgb(R, G, B);

    [JsonIgnore]
    public Color MediaColor => ToMedia();

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParseHex(string? text, out SerializableColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string t = text.TrimStart('#');
        if (t.Length == 6 && byte.TryParse(t.AsSpan(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte r)
            && byte.TryParse(t.AsSpan(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte g)
            && byte.TryParse(t.AsSpan(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte b))
        {
            color = new SerializableColor(r, g, b);
            return true;
        }

        return false;
    }

    public bool Equals(SerializableColor other) => R == other.R && G == other.G && B == other.B;
    public override bool Equals(object? obj) => obj is SerializableColor other && Equals(other);
    public override int GetHashCode() => (R << 16) | (G << 8) | B;
    public override string ToString() => ToHex();
}

public static class ColorMath
{
    /// <summary>HSV (h in [0,1)) to RGB bytes.</summary>
    public static void HsvToRgb(double h, double s, double v, out byte r, out byte g, out byte b)
    {
        h = h - Math.Floor(h); // wrap to [0,1)
        double c = v * s;
        double x = c * (1 - Math.Abs((h * 6 % 2) - 1));
        double m = v - c;
        double rr, gg, bb;
        int sector = (int)(h * 6) % 6;
        switch (sector)
        {
            case 0: rr = c; gg = x; bb = 0; break;
            case 1: rr = x; gg = c; bb = 0; break;
            case 2: rr = 0; gg = c; bb = x; break;
            case 3: rr = 0; gg = x; bb = c; break;
            case 4: rr = x; gg = 0; bb = c; break;
            default: rr = c; gg = 0; bb = x; break;
        }

        r = (byte)Math.Round((rr + m) * 255);
        g = (byte)Math.Round((gg + m) * 255);
        b = (byte)Math.Round((bb + m) * 255);
    }

    /// <summary>Sample a multi-color gradient at position p in [0,1].</summary>
    public static void SampleGradient(IReadOnlyList<SerializableColor> colors, double p, out byte r, out byte g, out byte b)
    {
        if (colors.Count == 0)
        {
            r = g = b = 0;
            return;
        }

        if (colors.Count == 1 || p <= 0)
        {
            r = colors[0].R;
            g = colors[0].G;
            b = colors[0].B;
            return;
        }

        if (p >= 1)
        {
            var last = colors[^1];
            r = last.R;
            g = last.G;
            b = last.B;
            return;
        }

        double scaled = p * (colors.Count - 1);
        int idx = (int)scaled;
        double frac = scaled - idx;
        var a = colors[idx];
        var c2 = colors[idx + 1];
        r = (byte)Math.Round(a.R + ((c2.R - a.R) * frac));
        g = (byte)Math.Round(a.G + ((c2.G - a.G) * frac));
        b = (byte)Math.Round(a.B + ((c2.B - a.B) * frac));
    }
}
