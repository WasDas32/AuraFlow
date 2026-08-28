# AuraFlow

Lightweight RGB control for your **Gigabyte RTX 3060 Eagle** and **ASUS TUF Z390 Pro-Gaming** -
inspired by SignalRGB and iCUE, built to stay out of your way.

- ~60-90 MB RAM, near-zero CPU for static colors (animated effects run on one low-priority thread)
- Per-device **stackable lighting layers** (iCUE-style): combine Static / Rainbow Cycle / Rainbow Wave /
  Breathing / Blink with per-layer color(s), speed, brightness, direction
- Live LED preview, dark modern UI
- System tray + start-with-Windows; the OpenRGB server auto-starts elevated via a scheduled task
  (**no UAC prompt at every login**)

## How it works

```
AuraFlow (WPF UI + effects engine)  --TCP localhost:6742-->  OpenRGB server (headless, elevated)
                                                                   |
                                                     NVIDIA i2c -> GPU LEDs
                                                     ITE EC    -> ASUS Aura zones
```

AuraFlow speaks the [OpenRGB](https://openrgb.org) SDK protocol directly. All hardware access is done
by OpenRGB, which already supports both of your boards reliably.

## First run

1. Build or use `publish\AuraFlow.exe` (.NET 8 Desktop Runtime required - already present if you built it).
2. Open **Settings**:
   - Click **Install OpenRGB automatically** (downloads OpenRGB 1.0rc3 from Codeberg into
     `%LOCALAPPDATA%\AuraFlow\OpenRGB`).
   - Click **Register** next to *Elevated logon task* and accept the one-time UAC prompt.
     This makes Windows start the OpenRGB server as admin at every login without asking again.
3. Restart AuraFlow (or click **Restart server**). Within ~10-20 seconds your GPU and motherboard
   should appear in the sidebar.
4. Pick a device, add layers, stack effects. Everything auto-saves to `%APPDATA%\AuraFlow\profile.json`.

### Notes / troubleshooting

- **iCUE**: fine to keep for Corsair gear, but if you enabled any motherboard/GPU control inside
  iCUE, turn that off - two programs fighting over the same LEDs causes flicker.
- **Armoury Crate / AI Suite / Gigabyte Control Center / RGB Fusion**: uninstall or disable their
  RGB services; they lock the SMBus/i2c that OpenRGB needs.
- If no devices show up: make sure OpenRGB itself (run manually as admin) detects them. If OpenRGB
  does not see them, AuraFlow cannot either.
- Close-to-tray is the default: the X button hides the window; use tray icon -> Exit to quit.
- Effects frame rate (Settings) trades smoothness for CPU. 30 fps is plenty; static colors cost
  almost nothing at any rate because frames are only pushed when something changes.

## Building

```
dotnet build AuraFlow.sln -c Release
dotnet publish src\AuraFlow.App -c Release -r win-x64 --self-contained false -o publish /p:PublishSingleFile=true
```

Protocol sanity test against a mock OpenRGB server (no hardware needed):

```
dotnet run --project tools\OpenRgbSmokeTest -c Debug -- mock 16742
```

## Roadmap

- Keyboard-reactive "Type Lighting" (iCUE-style)
- Lighting profiles switchable from the tray
- Temperature-reactive layers
- Per-zone layer targeting

## Credits

- [OpenRGB](https://gitlab.com/CalcProgrammer1/OpenRGB) (GPL) does all hardware communication.
- AuraFlow is a personal tool; distribute it under GPL-compatible terms if you share it.
