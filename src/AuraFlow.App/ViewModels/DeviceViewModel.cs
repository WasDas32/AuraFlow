using System.Collections.ObjectModel;
using System.Windows.Media;
using AuraFlow.App.Models;
using AuraFlow.App.Services;
using AuraFlow.OpenRgb;

namespace AuraFlow.App.ViewModels;

/// <summary>One glowing dot in the live preview.</summary>
public class LedDot : ObservableObject
{
    private Brush _brush = Brushes.Black;
    private byte _r = 0xFF, _g = 0xFF, _b = 0xFF; // matches Brushes.Black? no: black
    private bool _hasColor;

    public Brush Brush
    {
        get => _brush;
        set
        {
            if (Set(ref _brush, value))
            {
                _hasColor = true;
            }
        }
    }

    /// <summary>Updates only when the color actually changed (avoids per-frame allocations).</summary>
    public void SetRgb(byte r, byte g, byte b)
    {
        if (_hasColor && _r == r && _g == g && _b == b)
        {
            return;
        }

        _r = r;
        _g = g;
        _b = b;
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        Brush = brush;
    }
}

public class ZoneRow
{
    public required string Name { get; init; }
    public required ObservableCollection<LedDot> Dots { get; init; }
}

public sealed record ZoneChoice(int Index, string Name);

public sealed class DeviceViewModel : ObservableObject
{
    private readonly LightingEngine _engine;
    private byte[] _frame = Array.Empty<byte>();

    public DeviceConfig Config { get; }
    public OpenRgbDevice Device { get; }

    public string StableKey => Config.StableKey;
    public string Title => string.IsNullOrWhiteSpace(Config.DisplayName) ? Device.Name : Config.DisplayName;
    public string Subtitle => $"{Device.Vendor}  •  {Device.LedCount} LEDs";
    public bool Controllable { get; }
    public string TypeIcon => Device.Type switch
    {
        DeviceType.Gpu => "▲",
        DeviceType.Motherboard => "▦",
        DeviceType.Keyboard => "⌨",
        DeviceType.Mouse => "🖱",
        DeviceType.Headset => "🎧",
        DeviceType.Dram => "▮",
        DeviceType.LedStrip => "≡",
        DeviceType.Cooler => "❄",
        _ => "●",
    };

    public ObservableCollection<Layer> Layers => Config.Layers;

    public ObservableCollection<ZoneRow> Zones { get; } = new();

    private Layer? _selectedLayer;
    public Layer? SelectedLayer { get => _selectedLayer; set => Set(ref _selectedLayer, value); }

    private bool _editorExpanded;
    public bool EditorExpanded { get => _editorExpanded; set => Set(ref _editorExpanded, value); }

    public IReadOnlyList<ZoneChoice> TargetingZones { get; private set; } = new List<ZoneChoice> { new(-1, "All zones") };

    private static IReadOnlyList<ZoneChoice> BuildTargetingZones(OpenRgbDevice device)
    {
        var list = new List<ZoneChoice> { new(-1, "All zones") };
        foreach (var z in device.Zones.OrderBy(z => z.Index))
        {
            list.Add(new ZoneChoice(z.Index, z.Name));
        }
        return list;
    }

    private bool _enabled = true;
    public bool Enabled { get => _enabled; set { if (Set(ref _enabled, value)) { Config.Enabled = value; } } }

    public DeviceViewModel(LightingEngine engine, OpenRgbDevice device, DeviceConfig config)
    {
        _engine = engine;
        Device = device;
        Config = config;
        _enabled = config.Enabled;
        Controllable = device.DirectModeIndex >= 0 && device.LedCount > 0;

        int offset = 0;
        foreach (var zone in device.Zones)
        {
            var row = new ZoneRow { Name = zone.Name, Dots = new ObservableCollection<LedDot>() };
            for (int i = 0; i < zone.LedCount; i++)
            {
                row.Dots.Add(new LedDot());
            }

            offset += zone.LedCount;
            Zones.Add(row);
        }

        // Fallback when a device reports no zones but has LEDs.
        if (device.Zones.Count == 0 && device.LedCount > 0)
        {
            var row = new ZoneRow { Name = "LEDs", Dots = new ObservableCollection<LedDot>() };
            for (int i = 0; i < device.LedCount; i++)
            {
                row.Dots.Add(new LedDot());
            }

            Zones.Add(row);
        }

        _frame = new byte[device.LedCount * 3];
        TargetingZones = BuildTargetingZones(device);

        foreach (var l in config.Layers)
        {
            if (l.ZoneIndex >= 0 && !device.Zones.Any(z => z.Index == l.ZoneIndex))
            {
                l.ZoneIndex = -1;
            }
        }
    }

    /// <summary>Called ~15x/s from the UI timer.</summary>
    public void UpdatePreview()
    {
        if (!Controllable || _frame.Length == 0)
        {
            return;
        }

        if (!_engine.TryGetFrame(StableKey, _frame))
        {
            return;
        }

        int led = 0;
        foreach (var zone in Zones)
        {
            foreach (var dot in zone.Dots)
            {
                if (led * 3 + 2 < _frame.Length)
                {
                    dot.SetRgb(_frame[led * 3], _frame[(led * 3) + 1], _frame[(led * 3) + 2]);
                }

                led++;
            }
        }
    }
}
