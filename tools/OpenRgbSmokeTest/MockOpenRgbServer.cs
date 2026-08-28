using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using AuraFlow.OpenRgb;

namespace OpenRgbSmokeTest;

/// <summary>
/// Minimal in-process OpenRGB SDK server mock speaking the real wire protocol
/// (release_0.9): every reply reuses the request's packet id, and
/// DeviceListUpdated (100) is pushed unsolicited after client detection.
/// </summary>
public sealed class MockOpenRgbServer : IDisposable
{
    private const uint PktRequestControllerCount = 0;
    private const uint PktRequestControllerData = 1;
    private const uint PktRequestProtocolVersion = 40;
    private const uint PktSetClientName = 50;
    private const uint PktDeviceListUpdated = 100;
    private const uint PktUpdateLeds = 1050;
    private const uint PktSetCustomMode = 1100;
    private const uint PktUpdateMode = 1101;

    private readonly TcpListener _listener;
    private readonly List<TcpClient> _clients = new();
    private int _ledWrites;

    public int LedWrites => _ledWrites;

    public MockOpenRgbServer(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public void Start()
    {
        _listener.Start();
        var t = new Thread(AcceptLoop) { IsBackground = true };
        t.Start();
    }

    private async void AcceptLoop()
    {
        while (true)
        {
            TcpClient tcp;
            try
            {
                tcp = await _listener.AcceptTcpClientAsync();
            }
            catch
            {
                return;
            }

            tcp.NoDelay = true;
            lock (_clients)
            {
                _clients.Add(tcp);
            }

            var t = new Thread(() => ServeClient(tcp)) { IsBackground = true };
            t.Start();
        }
    }

    private void ServeClient(TcpClient tcp)
    {
        using var stream = tcp.GetStream();
        var writeLock = new object();
        var header = new byte[16];

        try
        {
            while (true)
            {
                if (!ReadExact(stream, header))
                {
                    return;
                }

                uint deviceId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
                uint packetId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
                byte[] payload = size > 0 ? new byte[size] : Array.Empty<byte>();
                if (size > 0 && !ReadExact(stream, payload))
                {
                    return;
                }

                switch (packetId)
                {
                    case PktSetClientName:
                        Console.WriteLine($"   [mock] client name '{ReadString(payload)}'");
                        ScheduleDeviceListUpdated(stream, writeLock);
                        break;

                    case PktRequestProtocolVersion:
                    {
                        uint requested = payload.Length >= 4
                            ? BinaryPrimitives.ReadUInt32LittleEndian(payload)
                            : 0u;
                        Reply(stream, writeLock, deviceId, packetId, w => WriteU32(w, Math.Min(requested, 5u)));
                        break;
                    }

                    case PktRequestControllerCount:
                        Reply(stream, writeLock, deviceId, packetId, w => WriteU32(w, 2));
                        break;

                    case PktRequestControllerData:
                        Reply(stream, writeLock, deviceId, packetId, w => SerializeDevice(w, (int)deviceId));
                        break;

                    case PktUpdateMode:
                    {
                        // rc3 requires the first u32 of the payload to equal the
                        // total payload size (same rule as the real server).
                        uint declared = payload.Length >= 4
                            ? BinaryPrimitives.ReadUInt32LittleEndian(payload)
                            : 0;
                        Console.WriteLine(declared == (uint)payload.Length
                            ? $"   [mock] UpdateMode dev={deviceId} (size ok)"
                            : $"   [mock] UpdateMode dev={deviceId} INVALID SIZE declared={declared} actual={payload.Length}");
                        break;
                    }

                    case PktSetCustomMode:
                        Console.WriteLine($"   [mock] SetCustomMode dev={deviceId}");
                        break;

                    case PktUpdateLeds:
                    {
                        uint declared = payload.Length >= 4
                            ? BinaryPrimitives.ReadUInt32LittleEndian(payload)
                            : 0;
                        if (declared != (uint)payload.Length)
                        {
                            Console.WriteLine($"   [mock] UpdateLeds INVALID SIZE declared={declared} actual={payload.Length}");
                        }

                        Interlocked.Increment(ref _ledWrites);
                        break;
                    }
                }
            }
        }
        catch
        {
            // client gone
        }
    }

    private static void ScheduleDeviceListUpdated(NetworkStream stream, object writeLock)
    {
        var t = new Thread(() =>
        {
            Thread.Sleep(300);
            try
            {
                SendPacket(stream, writeLock, 0, PktDeviceListUpdated, new List<byte>());
                Console.WriteLine("   [mock] DeviceListUpdated broadcast");
            }
            catch
            {
                // socket closed
            }
        })
        {
            IsBackground = true,
        };
        t.Start();
    }

    public static void SerializeDevice(List<byte> w, int index)
    {
        // rc3 device descriptor (RGBController::GetDeviceDescription), protocol
        // version 4: counts are u16, zones carry matrix_len + segments, the payload
        // is prefixed with a u32 data size. Serialize the body first, then prepend
        // the size so it covers the whole payload.
        if (index == 0)
        {
            WriteU32(w, (uint)DeviceType.Gpu);
            WriteString(w, "Gigabyte RTX 3060 Eagle");
            WriteString(w, "Gigabyte");
            WriteString(w, "Gigabyte RGB Fusion 2.0 GPU");
            WriteString(w, "1.0");
            WriteString(w, "SN-GPU-001");
            WriteString(w, "NVIDIA i2c 2");
            WriteU16(w, 5); // modes: Off, Static, Rainbow, Flash, Direct
            WriteU32(w, 3); // active mode
            SerializeMode(w, "Off", 0);
            SerializeMode(w, "Static", ModeFlags.HasModeSpecificColor);
            SerializeMode(w, "Rainbow", ModeFlags.HasSpeed);
            SerializeMode(w, "Flash", ModeFlags.HasSpeed | ModeFlags.HasModeSpecificColor);
            SerializeMode(w, "Direct", ModeFlags.HasPerLedColor);
            WriteU16(w, 1); // zones
            WriteString(w, "GPU");
            WriteU32(w, 1); // linear
            WriteU32(w, 1); // leds_min
            WriteU32(w, 24); // leds_max
            WriteU32(w, 24); // leds_count
            WriteU16(w, 0); // matrix_len - none
            WriteU16(w, 0); // segments - none
            WriteU16(w, 24); // num leds
            for (int i = 0; i < 24; i++)
            {
                WriteString(w, $"LED {i}");
                WriteU32(w, (uint)i);
            }

            WriteU16(w, 24); // colors
            for (int i = 0; i < 24; i++)
            {
                WriteU32(w, 0xFF000000);
            }
        }
        else
        {
            WriteU32(w, (uint)DeviceType.Motherboard);
            WriteString(w, "ASUS TUF Z390 Pro Gaming");
            WriteString(w, "ASUS");
            WriteString(w, "ASUS Aura Motherboard");
            WriteString(w, "1.0");
            WriteString(w, "SN-MB-002");
            WriteString(w, "ITE bus 1 address 0x4E");
            WriteU16(w, 4); // modes: Off, Static, Breathing, Direct
            WriteU32(w, 0); // active mode
            SerializeMode(w, "Off", 0);
            SerializeMode(w, "Static", ModeFlags.HasModeSpecificColor | ModeFlags.HasBrightness);
            SerializeMode(w, "Breathing", ModeFlags.HasSpeed | ModeFlags.HasModeSpecificColor);
            SerializeMode(w, "Direct", ModeFlags.HasPerLedColor);
            WriteU16(w, 2); // zones
            WriteString(w, "Audio");
            WriteU32(w, 0); // single
            WriteU32(w, 1); // leds_min
            WriteU32(w, 1); // leds_max
            WriteU32(w, 1); // leds_count
            WriteU16(w, 0); // matrix_len - none
            WriteU16(w, 0); // segments - none
            WriteString(w, "Addressable Strip");
            WriteU32(w, 1); // linear
            WriteU32(w, 1); // leds_min
            WriteU32(w, 100); // leds_max
            WriteU32(w, 7); // leds_count
            WriteU16(w, 0); // matrix_len - none
            WriteU16(w, 0); // segments - none
            WriteU16(w, 8); // total leds (1 + 7)
            for (int i = 0; i < 8; i++)
            {
                WriteString(w, $"LED {i}");
                WriteU32(w, (uint)i);
            }

            WriteU16(w, 8); // colors
            for (int i = 0; i < 8; i++)
            {
                WriteU32(w, 0xFF000000);
            }
        }

        PrependU32(w, (uint)(w.Count + 4));
    }

    private static void SerializeMode(List<byte> w, string name, ModeFlags flags)
    {
        // rc3 mode wire order (proto>=3): name, value, flags, speed_min, speed_max,
        // brightness_min, brightness_max, colors_min, colors_max, speed, brightness,
        // direction, color_mode, u16 mode color count, colors.
        WriteString(w, name);
        WriteU32(w, 0); // value
        WriteU32(w, (uint)flags);
        WriteU32(w, 0); // speed min
        WriteU32(w, 255); // speed max
        WriteU32(w, 0); // brightness min
        WriteU32(w, 100); // brightness max
        WriteU32(w, 0); // colors min
        WriteU32(w, 16); // colors max
        WriteU32(w, 128); // speed (current)
        WriteU32(w, 100); // brightness (current)
        WriteU32(w, 0); // direction
        WriteU32(w, flags.HasFlag(ModeFlags.HasModeSpecificColor)
            ? (uint)ColorMode.ModeSpecific
            : flags.HasFlag(ModeFlags.HasPerLedColor)
                ? (uint)ColorMode.PerLed
                : (uint)ColorMode.None);
        if (flags.HasFlag(ModeFlags.HasModeSpecificColor))
        {
            WriteU16(w, 1);
            WriteU32(w, 0xFF0000FF);
        }
        else
        {
            WriteU16(w, 0);
        }
    }

    private static void PrependU32(List<byte> list, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        list.InsertRange(0, b.ToArray());
    }

    private static void Reply(NetworkStream s, object writeLock, uint deviceId, uint packetId, Action<List<byte>> body)
    {
        var payload = new List<byte>();
        body(payload);
        SendPacket(s, writeLock, deviceId, packetId, payload);
    }

    private static void SendPacket(NetworkStream s, object writeLock, uint deviceId, uint packetId, List<byte> payload)
    {
        var header = new byte[16];
        header[0] = (byte)'O';
        header[1] = (byte)'R';
        header[2] = (byte)'G';
        header[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)payload.Count);

        lock (writeLock)
        {
            s.Write(header, 0, 16);
            if (payload.Count > 0)
            {
                s.Write(payload.ToArray(), 0, payload.Count);
            }

            s.Flush();
        }
    }

    private static void WriteU32(List<byte> list, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        list.AddRange(b.ToArray());
    }

    private static void WriteU16(List<byte> list, ushort v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        list.AddRange(b.ToArray());
    }

    private static void WriteString(List<byte> list, string s)
    {
        // u16 length INCLUDING the null terminator, bytes ending with '\0'.
        byte[] ascii = Encoding.ASCII.GetBytes(s);
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, (ushort)(ascii.Length + 1));
        list.AddRange(b.ToArray());
        list.AddRange(ascii);
        list.Add(0);
    }

    private static string ReadString(byte[] buf)
    {
        if (buf.Length < 2)
        {
            return string.Empty;
        }

        ushort len = BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(0, 2));
        if (len == 0 || buf.Length < 2 + len)
        {
            return string.Empty;
        }

        int contentLen = len;
        if (buf[2 + len - 1] == 0)
        {
            contentLen--;
        }

        return Encoding.ASCII.GetString(buf, 2, contentLen);
    }

    private static bool ReadExact(NetworkStream s, byte[] buffer)
    {
        int off = 0;
        while (off < buffer.Length)
        {
            int n = s.Read(buffer, off, buffer.Length - off);
            if (n <= 0)
            {
                return false;
            }

            off += n;
        }

        return true;
    }

    public void Dispose()
    {
        lock (_clients)
        {
            foreach (var c in _clients)
            {
                c.Close();
            }
        }

        _listener.Stop();
    }
}
