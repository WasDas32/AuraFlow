namespace AuraFlow.OpenRgb;

/// <summary>
/// OpenRGB SDK packet identifiers (matching NetworkProtocol.h of release 1.0rc3).
/// Replies to a request are sent with the SAME packet id as the request itself.
/// </summary>
internal static class PacketIds
{
    // Client -> Server (server replies reuse the same id)
    public const uint RequestControllerCount = 0;
    public const uint RequestControllerData  = 1;
    public const uint RequestProtocolVersion = 40;
    public const uint SetClientName          = 50;

    // Server -> Client (unsolicited)
    public const uint DeviceListChanged      = 100;

    // RGBController functions
    public const uint ResizeZone             = 1000;
    public const uint UpdateLeds             = 1050;
    public const uint UpdateZoneLeds         = 1051;
    public const uint UpdateSingleLed        = 1052;
    public const uint SetCustomMode          = 1100;
    public const uint UpdateMode             = 1101;
}

/// <summary>OpenRGB mode flag bits.</summary>
[Flags]
public enum ModeFlags : uint
{
    None                 = 0,
    HasSpeed             = 1u << 0,
    HasDirectionLr       = 1u << 1,
    HasDirectionUd       = 1u << 2,
    HasDirectionHv       = 1u << 3,
    HasBrightness        = 1u << 4,
    HasModeSpecificColor = 1u << 5,
    HasRandomColor       = 1u << 6,
    AutomaticSave        = 1u << 9,
    HasManualSave        = 1u << 10,
    HasPerLedColor       = 1u << 11,
    HasEffectId          = 1u << 12,
}

public enum ColorMode : uint
{
    None          = 0,
    PerLed        = 1,
    ModeSpecific  = 2,
    Random        = 3,
}

public enum DeviceType
{
    Motherboard = 0, Dram = 1, Gpu = 2, Cooler = 3, LedStrip = 4, Keyboard = 5,
    Mouse = 6, Mousemat = 7, Headset = 8, HeadsetStand = 9, Gamepad = 10, Light = 11,
    Speaker = 12, Virtual = 13, Storage = 14, Case = 15, Microphone = 16, Accessory = 17,
    Audio = 18, Other = 99,
}

public enum ZoneType { Single = 0, Linear = 1, Matrix = 2 }

public sealed class OpenRgbMode
{
    public required uint Value { get; init; }
    public required string Name { get; init; }
    public required ModeFlags Flags { get; init; }
    public required uint SpeedMin { get; init; }
    public required uint SpeedMax { get; init; }
    public required uint SpeedValue { get; init; }
    public required uint BrightnessMin { get; init; }
    public required uint BrightnessMax { get; init; }
    public required uint BrightnessValue { get; init; }
    public required uint ColorsMin { get; init; }
    public required uint ColorsMax { get; init; }
    public required int Direction { get; init; }
    public required ColorMode ColorMode { get; init; }
}

public sealed class OpenRgbZone
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required ZoneType Type { get; init; }
    public required int StartIndex { get; init; }
    public required int LedCount { get; init; }
}

public sealed class OpenRgbDevice
{
    public required int Index { get; init; }
    public required string Name { get; init; }
    public required string Vendor { get; init; }
    public required string Description { get; init; }
    public required string Version { get; init; }
    public required string Serial { get; init; }
    public required string Location { get; init; }
    public required DeviceType Type { get; init; }
    public required IReadOnlyList<OpenRgbMode> Modes { get; init; }
    public required int ActiveMode { get; init; }
    public required IReadOnlyList<OpenRgbZone> Zones { get; init; }
    public required IReadOnlyList<string> LedNames { get; init; }

    /// <summary>Total LEDs across all zones.</summary>
    public int LedCount => LedNames.Count;

    /// <summary>Stable-ish key used to persist per-device settings.</summary>
    public string StableKey => $"{Vendor}|{Name}|{Serial}|{Location}".ToLowerInvariant();

    /// <summary>Index of the best "Direct" mode for per-LED control, or -1.</summary>
    public int DirectModeIndex
    {
        get
        {
            for (int i = 0; i < Modes.Count; i++)
            {
                if (string.Equals(Modes[i].Name, "Direct", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            for (int i = 0; i < Modes.Count; i++)
            {
                if (Modes[i].Flags.HasFlag(ModeFlags.HasPerLedColor))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
