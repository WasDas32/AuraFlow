using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using AuraFlow.App.Services;

namespace AuraFlow.App.Models;

public enum EffectType
{
    Static,
    RainbowCycle,
    RainbowWave,
    Breathing,
    Blink,
}

public static class EffectTypeInfo
{
    public static string DisplayName(EffectType t) => t switch
    {
        EffectType.Static => "Static",
        EffectType.RainbowCycle => "Rainbow Cycle",
        EffectType.RainbowWave => "Rainbow Wave",
        EffectType.Breathing => "Breathing",
        EffectType.Blink => "Blink",
        _ => t.ToString(),
    };

    public static bool UsesColors(EffectType t) => t is EffectType.Static or EffectType.Breathing or EffectType.Blink;
    public static bool UsesSpeed(EffectType t) => t is not EffectType.Static;
    public static bool UsesDirection(EffectType t) => t is EffectType.RainbowCycle or EffectType.RainbowWave;
    public static bool UsesWaveCycles(EffectType t) => t == EffectType.RainbowWave;
    public static bool UsesDutyCycle(EffectType t) => t == EffectType.Blink;
}

/// <summary>Bindable wrapper for the effect type ComboBox.</summary>
public sealed class EffectOption
{
    public required EffectType Value { get; init; }
    public required string Label { get; init; }

    public override string ToString() => Label;

    public static IReadOnlyList<EffectOption> All { get; } = new[]
    {
        new EffectOption { Value = EffectType.Static, Label = "Static" },
        new EffectOption { Value = EffectType.RainbowCycle, Label = "Rainbow Cycle" },
        new EffectOption { Value = EffectType.RainbowWave, Label = "Rainbow Wave" },
        new EffectOption { Value = EffectType.Breathing, Label = "Breathing" },
        new EffectOption { Value = EffectType.Blink, Label = "Blink" },
    };
}

/// <summary>A single lighting layer. Layers stack bottom-up; higher entries composite over lower ones.</summary>
public class Layer : INotifyPropertyChanged
{
    private string _name = "Layer";
    private EffectType _type = EffectType.Static;
    private bool _enabled = true;
    private double _speed = 50;
    private double _brightness = 100;
    private bool _reverse;
    private double _waveCycles = 2;
    private double _dutyCycle = 50;
    private int _zoneIndex = -1;

    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get => _name; set => Set(ref _name, value); }

    public EffectType Type
    {
        get => _type;
        set
        {
            Set(ref _type, value);
            if (value == EffectType.Static)
            {
                while (Colors.Count > 1)
                {
                    Colors.RemoveAt(Colors.Count - 1);
                }
            }
        }
    }

    /// <summary>-1 = all zones; >= 0 = device zone Index.</summary>
    public int ZoneIndex { get => _zoneIndex; set => Set(ref _zoneIndex, value); }
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }
    /// <summary>0..100. Maps to a full pattern period of 12s .. 1.2s.</summary>
    public double Speed { get => _speed; set => Set(ref _speed, Math.Clamp(value, 0, 100)); }
    /// <summary>0..100.</summary>
    public double Brightness { get => _brightness; set => Set(ref _brightness, Math.Clamp(value, 0, 100)); }
    public bool Reverse { get => _reverse; set => Set(ref _reverse, value); }
    /// <summary>Rainbow Wave: number of hue cycles across the strip (0.25..8).</summary>
    public double WaveCycles { get => _waveCycles; set => Set(ref _waveCycles, Math.Clamp(value, 0.25, 8)); }
    /// <summary>Blink: on-fraction of the period in percent (5..95).</summary>
    public double DutyCycle { get => _dutyCycle; set => Set(ref _dutyCycle, Math.Clamp(value, 5, 95)); }

    public ObservableCollection<SerializableColor> Colors { get; set; } = new() { new SerializableColor(255, 255, 255) };

    [JsonIgnore]
    public string TypeDisplay => EffectTypeInfo.DisplayName(Type);

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplaySummary)));
        }
    }

    [JsonIgnore]
    public string DisplaySummary => $"{TypeDisplay}";
}

/// <summary>Per-device configuration persisted in the profile.</summary>
public class DeviceConfig : INotifyPropertyChanged
{
    private bool _enabled = true;
    private ObservableCollection<Layer> _layers = new();

    public required string StableKey { get; set; }
    public string DisplayName { get; set; } = "";

    /// <summary>Master switch: whether AuraFlow drives this device at all.</summary>
    public bool Enabled { get => _enabled; set => Set(ref _enabled, value); }

    /// <summary>Bottom-to-top layer stack.</summary>
    public ObservableCollection<Layer> Layers { get => _layers; set => Set(ref _layers, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        if (!EqualityComparer<T>.Default.Equals(field, value))
        {
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(prop));
        }
    }
}

/// <summary>Root profile document.</summary>
public class ProfileDocument
{
    public int Version { get; set; } = 1;
    public List<DeviceConfig> Devices { get; set; } = new();
}
