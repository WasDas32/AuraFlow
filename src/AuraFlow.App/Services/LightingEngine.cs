using System.Collections.ObjectModel;
using System.Diagnostics;
using AuraFlow.App.Models;
using AuraFlow.OpenRgb;

namespace AuraFlow.App.Services;

/// <summary>
/// Renders the per-device layer stacks into LED buffers and pushes them to OpenRGB.
/// Single background thread, below-normal priority; static setups cost near-zero CPU
/// because frames are only pushed when content actually changes.
/// </summary>
public sealed class LightingEngine : IDisposable
{
    private class DeviceState
    {
        public required int Index;
        public required string StableKey;
        public required int LedCount;
        public required bool ControlEnabled;
        public List<Layer> Layers = new();
        public volatile byte[] Front = Array.Empty<byte>();
        public byte[] Back = Array.Empty<byte>();
        public byte[] Scratch = Array.Empty<byte>();
        public uint LastHash;
        public bool PushedOnce;
    }

    private readonly OpenRgbClient _client;
    private readonly Func<IReadOnlyList<DeviceConfig>> _getConfigs;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _stateLock = new();

    private List<DeviceState> _states = new();
    private volatile bool _configDirty = true;
    private volatile bool _blackout;

    public event Action? DevicesChanged;
    public event Action? Connected;
    public event Action? Disconnected;

    public OpenRgbClient Client => _client;
    public bool Blackout => _blackout;

    public LightingEngine(OpenRgbClient client, Func<IReadOnlyList<DeviceConfig>> getConfigs)
    {
        _client = client;
        _getConfigs = getConfigs;

        _client.Connected += () => { MarkConfigDirty(); SafeInvoke(Connected); };
        _client.Disconnected += () => SafeInvoke(Disconnected);

        _client.DevicesChanged += () =>
        {
            RebuildStates();
            SafeInvoke(DevicesChanged);
        };

        _thread = new Thread(RenderLoop)
        {
            IsBackground = true,
            Name = "AuraFlow.Effects",
            Priority = ThreadPriority.BelowNormal,
        };
    }

    public void Start()
    {
        _thread.Start();
        _client.Start();
    }

    /// <summary>Call whenever layer collections/structure change so the engine re-reads configs.</summary>
    public void MarkConfigDirty() => _configDirty = true;

    public void SetBlackout(bool value)
    {
        if (_blackout == value)
        {
            return;
        }

        _blackout = value;
        _configDirty = true;
    }

    // ------------------------------------------------------------------ loop

    private void RenderLoop()
    {
        var ct = _cts.Token;
        double fps = SettingsService.LoadOrDefault().Fps;
        long lastAdjust = 0;

        while (!ct.IsCancellationRequested)
        {
            var sw = Stopwatch.StartNew();

            try
            {
                Tick();
            }
            catch
            {
                // never let the render thread die
            }

            // pick up FPS setting changes occasionally
            if (_clock.ElapsedMilliseconds - lastAdjust > 2000)
            {
                fps = SettingsService.LoadOrDefault().Fps;
                lastAdjust = _clock.ElapsedMilliseconds;
            }

            double intervalMs = 1000.0 / Math.Clamp(fps, 5, 60);
            int sleep = (int)(intervalMs - sw.Elapsed.TotalMilliseconds);
            if (sleep > 0)
            {
                Thread.Sleep(sleep);
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    private void Tick()
    {
        if (_configDirty)
        {
            SnapshotConfigs();
            _configDirty = false;
        }

        double t = _clock.ElapsedTotalSeconds();

        List<DeviceState> states;
        lock (_stateLock)
        {
            states = _states;
        }

        foreach (var ds in states)
        {
            if (ds.Layers.Count == 0 || !ds.ControlEnabled)
            {
                continue;
            }

            bool animated = !_blackout && ds.Layers.Any(l => l.Enabled && l.Type != EffectType.Static);

            if (!animated && ds.PushedOnce && !_configDirty)
            {
                continue; // static frame already on hardware
            }

            Compose(ds, t, _blackout);

            uint h = Fnv1a(ds.Back);
            if (animated || h != ds.LastHash || !ds.PushedOnce)
            {
                _client.UpdateLeds(ds.Index, ds.Back);
                ds.LastHash = h;
                ds.PushedOnce = true;
            }

            // publish for preview
            (ds.Front, ds.Back) = (ds.Back, ds.Front);
        }
    }

    private void SnapshotConfigs()
    {
        var configs = _getConfigs();
        var existingByKey = _states.ToDictionary(s => s.StableKey);
        var newList = new List<DeviceState>(configs.Count);

        foreach (var cfg in configs)
        {
            var dev = _client.Devices.FirstOrDefault(d => d.StableKey == cfg.StableKey);
            if (dev is null || dev.DirectModeIndex < 0 || dev.LedCount <= 0)
            {
                continue;
            }

            if (!existingByKey.TryGetValue(cfg.StableKey, out var ds))
            {
                ds = new DeviceState
                {
                    Index = dev.Index,
                    StableKey = cfg.StableKey,
                    LedCount = dev.LedCount,
                    ControlEnabled = cfg.Enabled,
                    Back = new byte[dev.LedCount * 3],
                    Scratch = new byte[dev.LedCount * 4],
                    Front = new byte[dev.LedCount * 3],
                };
            }
            else
            {
                ds.Index = dev.Index;
                ds.ControlEnabled = cfg.Enabled;
            }

            List<Layer> layers;
            try
            {
                layers = cfg.Layers.Where(l => l.Enabled).ToList();
            }
            catch
            {
                // collection mutated mid-enumeration; retry next tick
                _configDirty = true;
                return;
            }

            ds.Layers = layers;
            ds.PushedOnce = false;
            newList.Add(ds);

            if (cfg.Enabled)
            {
                try
                {
                    _client.SetDirectMode(dev.Index);
                }
                catch
                {
                }
            }
        }

        lock (_stateLock)
        {
            _states = newList;
        }
    }

    private void RebuildStates()
    {
        _configDirty = true;
    }

    // -------------------------------------------------------------- compositing

    private static void Compose(DeviceState ds, double t, bool blackout)
    {
        var buf = ds.Back;
        var scratch = ds.Scratch;
        Array.Clear(buf, 0, buf.Length);

        if (blackout)
        {
            return;
        }

        int n = ds.LedCount;
        foreach (var layer in ds.Layers)
        {
            if (!layer.Enabled)
            {
                continue;
            }

            RenderLayer(layer, n, t, scratch);

            double inv = 1.0 / 255.0;
            for (int i = 0; i < n; i++)
            {
                double a = scratch[(i * 4) + 3] * inv;
                if (a <= 0)
                {
                    continue;
                }

                int o = i * 3;
                buf[o] = (byte)Math.Min(255, buf[o] + (scratch[o] * a));
                buf[o + 1] = (byte)Math.Min(255, buf[o + 1] + (scratch[o + 1] * a));
                buf[o + 2] = (byte)Math.Min(255, buf[o + 2] + (scratch[o + 2] * a));
            }
        }
    }

    private static void RenderLayer(Layer layer, int n, double t, byte[] outRgba)
    {
        double period = SpeedToPeriod(layer.Speed);
        double brightnessAlpha = layer.Brightness * 2.55;
        var colors = layer.Colors;
        double dir = layer.Reverse ? -1 : 1;

        switch (layer.Type)
        {
            case EffectType.Static:
            {
                for (int i = 0; i < n; i++)
                {
                    double p = n > 1 ? (double)i / (n - 1) : 0;
                    ColorMath.SampleGradient(colors, p, out byte r, out byte g, out byte b);
                    WritePixel(outRgba, i, r, g, b, brightnessAlpha);
                }

                break;
            }

            case EffectType.RainbowCycle:
            {
                double baseHue = dir * t / period;
                for (int i = 0; i < n; i++)
                {
                    double hue = Wrap01(baseHue + ((double)i / n));
                    HsvWrite(outRgba, i, hue, brightnessAlpha);
                }

                break;
            }

            case EffectType.RainbowWave:
            {
                double baseHue = dir * t / period;
                double cycles = layer.WaveCycles;
                for (int i = 0; i < n; i++)
                {
                    double hue = Wrap01(baseHue + (i * cycles / n));
                    HsvWrite(outRgba, i, hue, brightnessAlpha);
                }

                break;
            }

            case EffectType.Breathing:
            {
                double phase = (t / period) % 1;
                double wave = 0.5 - (0.5 * Math.Cos(2 * Math.PI * phase)); // 0..1..0
                double cyclePos = t / period;
                SampleCycling(colors, cyclePos, out byte r, out byte g, out byte b);
                byte a = (byte)Math.Round(wave * brightnessAlpha);
                for (int i = 0; i < n; i++)
                {
                    WritePixel(outRgba, i, r, g, b, a);
                }

                break;
            }

            case EffectType.Blink:
            {
                double cyclePos = t / period;
                double phase = cyclePos % 1;
                bool on = phase < layer.DutyCycle / 100.0;
                SampleCycling(colors, Math.Floor(cyclePos), out byte r, out byte g, out byte b);
                byte aOn = (byte)Math.Round(brightnessAlpha);
                for (int i = 0; i < n; i++)
                {
                    WritePixel(outRgba, i, r, g, b, on ? aOn : (byte)0);
                }

                break;
            }
        }
    }

    private static void SampleCycling(IReadOnlyList<SerializableColor> colors, double pos, out byte r, out byte g, out byte b)
    {
        if (colors.Count == 0)
        {
            r = g = b = 0;
            return;
        }

        if (colors.Count == 1)
        {
            r = colors[0].R;
            g = colors[0].G;
            b = colors[0].B;
            return;
        }

        double scaled = Wrap01(pos) * colors.Count;
        int idx = (int)scaled % colors.Count;
        double frac = scaled - Math.Floor(scaled);
        var a = colors[idx];
        var c2 = colors[(idx + 1) % colors.Count];
        r = (byte)Math.Round(a.R + ((c2.R - a.R) * frac));
        g = (byte)Math.Round(a.G + ((c2.G - a.G) * frac));
        b = (byte)Math.Round(a.B + ((c2.B - a.B) * frac));
    }

    private static void HsvWrite(byte[] buf, int i, double hue, double alpha)
    {
        ColorMath.HsvToRgb(hue, 1, 1, out byte r, out byte g, out byte b);
        WritePixel(buf, i, r, g, b, alpha);
    }

    private static void WritePixel(byte[] buf, int i, byte r, byte g, byte b, double alpha)
    {
        int o = i * 4;
        buf[o] = r;
        buf[o + 1] = g;
        buf[o + 2] = b;
        buf[o + 3] = (byte)Math.Clamp(Math.Round(alpha), 0, 255);
    }

    /// <summary>Speed 0..100 -> period 12s..1.2s.</summary>
    private static double SpeedToPeriod(double speed)
    {
        double s = Math.Clamp(speed, 0, 100) / 100.0;
        return 12.0 - (10.8 * s);
    }

    private static double Wrap01(double v) => v - Math.Floor(v);

    private static uint Fnv1a(byte[] data)
    {
        uint hash = 2166136261;
        foreach (byte b in data)
        {
            hash ^= b;
            hash *= 16777619;
        }

        return hash;
    }

    // ------------------------------------------------------------------ preview

    /// <summary>Copies the latest composed frame for a device (for UI preview).</summary>
    public bool TryGetFrame(string stableKey, Span<byte> destination)
    {
        List<DeviceState> states;
        lock (_stateLock)
        {
            states = _states;
        }

        var ds = states.FirstOrDefault(s => s.StableKey == stableKey);
        if (ds is null)
        {
            return false;
        }

        var front = ds.Front;
        int len = Math.Min(front.Length, destination.Length);
        if (len == 0)
        {
            return false;
        }

        front.AsSpan(0, len).CopyTo(destination);
        return true;
    }

    // -------------------------------------------------------------------- util

    private static void SafeInvoke(Action? a)
    {
        try
        {
            a?.Invoke();
        }
        catch
        {
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _client.Dispose();
        _cts.Dispose();
    }
}

internal static class StopwatchExt
{
    public static double ElapsedTotalSeconds(this Stopwatch sw) => sw.ElapsedTicks / (double)Stopwatch.Frequency;
}
