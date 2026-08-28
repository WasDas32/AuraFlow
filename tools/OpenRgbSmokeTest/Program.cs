using AuraFlow.OpenRgb;
using OpenRgbSmokeTest;

// --------------------------------------------------------------------- dump mode
// Read-only device descriptor dump. Usage: OpenRgbSmokeTest dump [port]
if (args.Length > 0 && args[0] == "dump")
{
    int dumpPort = 6742;
    if (args.Length > 1 && int.TryParse(args[1], out var dp))
    {
        dumpPort = dp;
    }

    using var dumpClient = new OpenRgbClient("127.0.0.1", dumpPort);
    var dumpConnected = new TaskCompletionSource();
    dumpClient.Connected += () => dumpConnected.TrySetResult();
    dumpClient.Start();

    var dumpTimeout = Task.Delay(8000);
    if (await Task.WhenAny(dumpConnected.Task, dumpTimeout) == dumpTimeout)
    {
        Console.WriteLine("FAIL: no connection within 8s");
        return 1;
    }

    await Task.Delay(500); // let device list populate

    Console.WriteLine($"Connected. Protocol v{dumpClient.NegotiatedProtocolVersion}");
    Console.WriteLine($"Devices: {dumpClient.Devices.Count}");
    Console.WriteLine();

    foreach (var dev in dumpClient.Devices)
    {
        Console.WriteLine($"[{dev.Index}] {dev.Name}");
        Console.WriteLine($"  Type:         {dev.Type}");
        Console.WriteLine($"  Vendor:       {dev.Vendor}");
        Console.WriteLine($"  Description:  {dev.Description}");
        Console.WriteLine($"  Version:      {dev.Version}");
        Console.WriteLine($"  Serial:       {dev.Serial}");
        Console.WriteLine($"  Location:     {dev.Location}");
        Console.WriteLine($"  LedCount:     {dev.LedCount}");
        Console.WriteLine($"  ActiveMode:   {dev.ActiveMode}");
        Console.WriteLine($"  DirectModeIdx:{dev.DirectModeIndex}");
        Console.WriteLine($"  StableKey:    {dev.StableKey}");

        Console.WriteLine($"  Modes ({dev.Modes.Count}):");
        for (int i = 0; i < dev.Modes.Count; i++)
        {
            var m = dev.Modes[i];
            string active = i == dev.ActiveMode ? " *" : "";
            Console.WriteLine($"    [{i}] {m.Name} val={m.Value} flags=0x{((uint)m.Flags):X4} colorMode={m.ColorMode}{active}");
        }

        Console.WriteLine($"  Zones ({dev.Zones.Count}):");
        foreach (var z in dev.Zones)
        {
            Console.WriteLine($"    [{z.Index}] {z.Name} type={z.Type} start={z.StartIndex} ledCount={z.LedCount}");
        }

        Console.WriteLine();
    }

    return 0;
}

// Smoke test for AuraFlow.OpenRgb against either a real OpenRGB server or the built-in mock.
// Usage: OpenRgbSmokeTest [port] | mock [port]
bool useMock = args.Length > 0 && args[0] == "mock";
int port = 6742;
if (useMock)
{
    if (args.Length > 1 && int.TryParse(args[1], out var mp))
    {
        port = mp;
    }
}
else if (args.Length > 0 && int.TryParse(args[0], out var p))
{
    port = p;
}

MockOpenRgbServer? mock = null;
if (useMock)
{
    mock = new MockOpenRgbServer(port);
    mock.Start();
    Console.WriteLine($"[mock] listening on {port}");
}

using var client = new OpenRgbClient("127.0.0.1", port);
var connected = new TaskCompletionSource();
client.Connected += () => connected.TrySetResult();

client.Start();

var timeout = Task.Delay(8000);
if (await Task.WhenAny(connected.Task, timeout) == timeout)
{
    Console.WriteLine("FAIL: no connection within 8s");
    return 1;
}

Console.WriteLine($"Connected. Protocol v{client.NegotiatedProtocolVersion}");
await Task.Delay(500);

int failures = 0;

if (useMock)
{
    // Mock exposes exactly 2 devices (GPU + motherboard) for strict parse checks.
    if (client.Devices.Count != 2)
    {
        Console.WriteLine("FAIL: expected 2 devices");
        failures++;
    }

    var gpu = client.Devices.FirstOrDefault(d => d.Type == DeviceType.Gpu);
    var mb = client.Devices.FirstOrDefault(d => d.Type == DeviceType.Motherboard);

    if (gpu is null || mb is null)
    {
        Console.WriteLine("FAIL: missing GPU or motherboard device");
        failures++;
    }
    else
    {
        if (gpu.LedCount != 24 || gpu.DirectModeIndex != 4)
        {
            Console.WriteLine($"FAIL: GPU parse wrong leds={gpu.LedCount} direct={gpu.DirectModeIndex}");
            failures++;
        }

        if (mb.LedCount != 8 || mb.Zones.Count != 2 || mb.Zones[1].StartIndex != 1)
        {
            Console.WriteLine($"FAIL: MB parse wrong leds={mb.LedCount} zones={mb.Zones.Count}");
            failures++;
        }

        // Direct mode + LED write round-trip
        client.SetDirectMode(gpu.Index);
        var rgb = new byte[gpu.LedCount * 3];
        for (int i = 0; i < gpu.LedCount; i++)
        {
            rgb[i * 3] = (byte)(i * 8);
            rgb[(i * 3) + 1] = 16;
            rgb[(i * 3) + 2] = 32;
        }

        client.UpdateLeds(gpu.Index, rgb);
        await Task.Delay(300);
        Console.WriteLine($"[mock] led writes received: {mock!.LedWrites}");

        if (mock.LedWrites == 0)
        {
            Console.WriteLine("FAIL: mock received no UpdateLeds packet");
            failures++;
        }
    }
}
else
{
    // Real server: hardware-dependent. Verify devices parse, then push a frame to
    // the first Direct-capable device to prove the packet wire format is accepted
    // (a rejected UpdateLeds/UpdateMode drops the connection).
    if (client.Devices.Count == 0)
    {
        Console.WriteLine("FAIL: no devices enumerated");
        failures++;
    }

    var direct = client.Devices.FirstOrDefault(d => d.DirectModeIndex >= 0);
    if (direct is null)
    {
        Console.WriteLine("WARN: no Direct-capable device found; skipping frame push");
    }
    else
    {
        client.SetDirectMode(direct.Index);
        var rgb = new byte[direct.LedCount * 3];
        for (int i = 0; i < direct.LedCount; i++)
        {
            rgb[i * 3] = (byte)(i * 5);
            rgb[(i * 3) + 1] = 32;
            rgb[(i * 3) + 2] = 64;
        }

        client.UpdateLeds(direct.Index, rgb);
        await Task.Delay(300);
        Console.WriteLine($"Pushed frame to [{direct.Index}] {direct.Name}");
    }
}

if (failures == 0)
{
    Console.WriteLine("OK");
    return 0;
}

Console.WriteLine($"{failures} FAILURES");
return 2;
