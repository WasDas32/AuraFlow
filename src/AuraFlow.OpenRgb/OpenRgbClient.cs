using System.Buffers.Binary;
using System.Net.Sockets;

namespace AuraFlow.OpenRgb;

/// <summary>
/// Client for the OpenRGB SDK network protocol (OpenRGB 1.0rc3, wire format per
/// RGBController.cpp GetDeviceDescription / GetColorDescription /
/// GetModeDescription). Maintains a single persistent connection to a local
/// OpenRGB server, enumerates devices and pushes per-LED colors.
///
/// Architecture: exactly one reader thread owns the socket read side. Requests are
/// serialized through a semaphore; their responses are matched by packet id via a
/// pending-slot. Pushed notifications (device list changed) are handled inline.
/// </summary>
public sealed class OpenRgbClient : IDisposable
{
    public const int ProtocolVersion = 4;

    private const int HeaderSize = 16;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private readonly object _sendLock = new();
    private readonly object _pendingLock = new();
    private readonly SemaphoreSlim _requestLock = new(1, 1);
    private CancellationTokenSource? _cts;
    private volatile bool _disposed;
    private volatile bool _handshakeComplete;

    // Pending request slot (at most one outstanding request).
    private uint _pendingPacketId;
    private TaskCompletionSource<byte[]>? _pendingTcs;
    private int _refreshInFlight;

    private readonly List<OpenRgbDevice> _devices = new();

    public string Host { get; }
    public int Port { get; }
    public string ClientName { get; set; } = "AuraFlow";

    public int NegotiatedProtocolVersion { get; private set; }

    /// <summary>Snapshot of the current device list.</summary>
    public IReadOnlyList<OpenRgbDevice> Devices
    {
        get
        {
            lock (_devices)
            {
                return _devices.ToArray();
            }
        }
    }

    public bool IsConnected => _tcp is { Connected: true };

    public event Action? Connected;
    public event Action? Disconnected;
    public event Action? DevicesChanged;

    /// <summary>Diagnostic messages (connects, refreshes, protocol oddities).</summary>
    public event Action<string>? LogMessage;

    public OpenRgbClient(string host = "127.0.0.1", int port = 6742)
    {
        Host = host;
        Port = port;
    }

    /// <summary>Starts the background connection loop (connect, handshake, reconnect on loss).</summary>
    public void Start()
    {
        ThrowIfDisposed();
        var cts = new CancellationTokenSource();
        var old = Interlocked.Exchange(ref _cts, cts);
        old?.Cancel();
        old?.Dispose();

        var thread = new Thread(() => RunLoop(cts.Token))
        {
            IsBackground = true,
            Name = "AuraFlow.OpenRgb",
            Priority = ThreadPriority.BelowNormal,
        };
        thread.Start();
    }

    private async void RunLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                await ConnectOnceAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // fall through to backoff/retry
            }

            CloseSocket();
            _handshakeComplete = false;
            SafeRaiseDisconnected();

            try
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task ConnectOnceAsync(CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(Host, Port, ct).ConfigureAwait(false);
        _tcp = tcp;
        _stream = tcp.GetStream();

        // Start the single reader thread first; everything else goes through it.
        var readerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var readerThread = new Thread(() => ReaderLoop(readerCts.Token))
        {
            IsBackground = true,
            Name = "AuraFlow.OpenRgb.Reader",
            Priority = ThreadPriority.BelowNormal,
        };
        readerThread.Start();

        try
        {
            // Handshake: identify ourselves, negotiate protocol, enumerate devices.
            var namePayload = new List<byte>(64);
            ProtocolWriter.WriteString(namePayload, ClientName);
            SendPacket(PacketIds.SetClientName, 0, namePayload);

            uint negotiated = await RequestAsync(
                PacketIds.RequestProtocolVersion,
                payload => ProtocolWriter.WriteU32(payload, ProtocolVersion),
                static r => r.ReadU32(),
                ct).ConfigureAwait(false);
            NegotiatedProtocolVersion = (int)negotiated;
            LogMessage?.Invoke($"Connected to {Host}:{Port}, protocol v{NegotiatedProtocolVersion}");

            uint count = await RequestAsync(
                PacketIds.RequestControllerCount,
                static _ => { },
                static r => r.ReadU32(),
                ct).ConfigureAwait(false);
            LogMessage?.Invoke($"Server reports {count} controller(s)");

            var devices = new List<OpenRgbDevice>((int)count);
            for (uint i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var dev = await RequestAsync(
                    PacketIds.RequestControllerData,
                    p => ProtocolWriter.WriteU32(p, ProtocolVersion),
                    r => ParseDevice((int)i, r),
                    ct,
                    deviceId: i).ConfigureAwait(false);
                devices.Add(dev);
            }

            lock (_devices)
            {
                _devices.Clear();
                _devices.AddRange(devices);
            }

            SafeRaiseConnected();
            _handshakeComplete = true;
            SafeRaiseDevicesChanged();

            // Safety net: OpenRGB may finish detection after we connected (it sends
            // DeviceListChanged, but re-poll periodically in case that packet is missed).
            int refreshCount = devices.Count;
            var refreshTimer = new Timer(
                _ =>
                {
                    if (Interlocked.Exchange(ref _refreshInFlight, 1) == 0)
                    {
                        try
                        {
                            RefreshDevicesAsync(CancellationToken.None).GetAwaiter().GetResult();
                            var newCount = Devices.Count;
                            if (newCount != refreshCount)
                            {
                                LogMessage?.Invoke($"Periodic refresh: {refreshCount} -> {newCount} device(s)");
                                refreshCount = newCount;
                            }
                        }
                        catch
                        {
                            // connection dead - reader loop tears everything down
                        }
                        finally
                        {
                            Interlocked.Exchange(ref _refreshInFlight, 0);
                        }
                    }
                },
                null,
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(15));

            try
            {
                // Block this RunLoop iteration until the connection dies.
                await readerThread.JoinAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                refreshTimer.Dispose();
            }
        }
        finally
        {
            readerCts.Cancel();
            CloseSocket();
        }
    }

    /// <summary>Single owner of the socket read side.</summary>
    private void ReaderLoop(CancellationToken ct)
    {
        var stream = _stream!;
        var header = new byte[HeaderSize];

        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!ReadExact(stream, header))
                {
                    return;
                }

                if (header[0] != (byte)'O' || header[1] != (byte)'R' || header[2] != (byte)'G' || header[3] != (byte)'B')
                {
                    return; // desync
                }

                uint packetId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
                if (size > 16 * 1024 * 1024)
                {
                    return; // absurd size -> desync
                }

                byte[] payload = size > 0 ? new byte[size] : Array.Empty<byte>();
                if (size > 0 && !ReadExact(stream, payload))
                {
                    return;
                }

                TaskCompletionSource<byte[]>? tcs = null;
                lock (_pendingLock)
                {
                    if (_pendingTcs is not null && _pendingPacketId == packetId)
                    {
                        tcs = _pendingTcs;
                        _pendingTcs = null;
                        _pendingPacketId = 0;
                    }
                }

                if (tcs is not null)
                {
                    tcs.TrySetResult(payload);
                }
                else if (packetId == PacketIds.DeviceListChanged && _handshakeComplete)
                {
                    _ = Task.Run(() => RefreshDevicesAsync(ct), ct);
                }

                // anything else: unsolicited/unknown -> drop
            }
        }
        catch
        {
            // socket error -> connection considered dead
        }
    }

    private async Task RefreshDevicesAsync(CancellationToken ct)
    {
        try
        {
            await _requestLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                uint count = await RequestLockedAsync(
                    PacketIds.RequestControllerCount,
                    static _ => { },
                    static r => r.ReadU32(),
                    ct).ConfigureAwait(false);

                var devices = new List<OpenRgbDevice>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    var dev = await RequestLockedAsync(
                        PacketIds.RequestControllerData,
                        p => ProtocolWriter.WriteU32(p, ProtocolVersion),
                        r => ParseDevice((int)i, r),
                        ct,
                        deviceId: i).ConfigureAwait(false);
                    devices.Add(dev);
                }

                lock (_devices)
                {
                    _devices.Clear();
                    _devices.AddRange(devices);
                }

                LogMessage?.Invoke($"Device list refreshed: {devices.Count} device(s)");
                SafeRaiseDevicesChanged();
            }
            finally
            {
                _requestLock.Release();
            }
        }
        catch
        {
            // ignore - main loop handles disconnects
        }
    }

    // ---------------------------------------------------------------- requests

    private Task<T> RequestAsync<T>(
        uint packetId,
        Action<List<byte>> writePayload,
        Func<ProtocolReader, T> parseResponse,
        CancellationToken ct,
        uint deviceId = 0)
    {
        return ExecuteRequestAsync(lockGate: true, packetId, writePayload, parseResponse, ct, deviceId);
    }

    /// <summary>Callers already holding <see cref="_requestLock"/> pass lockGate: false.</summary>
    private Task<T> RequestLockedAsync<T>(
        uint packetId,
        Action<List<byte>> writePayload,
        Func<ProtocolReader, T> parseResponse,
        CancellationToken ct,
        uint deviceId = 0)
    {
        return ExecuteRequestAsync(lockGate: false, packetId, writePayload, parseResponse, ct, deviceId);
    }

    private async Task<T> ExecuteRequestAsync<T>(
        bool lockGate,
        uint packetId,
        Action<List<byte>> writePayload,
        Func<ProtocolReader, T> parseResponse,
        CancellationToken ct,
        uint deviceId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_stream is null)
        {
            throw new InvalidOperationException("Not connected.");
        }

        var payload = new List<byte>(64);
        writePayload(payload);

        if (lockGate)
        {
            await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        }

        try
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_pendingLock)
            {
                // 0.9 replies reuse the request's packet id - key the pending slot on it.
                _pendingPacketId = packetId;
                _pendingTcs = tcs;
            }

            ct.Register(() => tcs.TrySetCanceled(ct));

            SendPacket(packetId, deviceId, payload);

            byte[] body = await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
            return parseResponse(new ProtocolReader(body));
        }
        finally
        {
            lock (_pendingLock)
            {
                _pendingTcs = null;
                _pendingPacketId = 0;
            }

            if (lockGate)
            {
                _requestLock.Release();
            }
        }
    }

    private void SendPacket(uint packetId, uint deviceId, IReadOnlyList<byte> payload)
    {
        var stream = _stream ?? throw new InvalidOperationException("Not connected.");
        var header = new byte[HeaderSize];
        header[0] = (byte)'O';
        header[1] = (byte)'R';
        header[2] = (byte)'G';
        header[3] = (byte)'B';
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4, 4), deviceId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), packetId);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)payload.Count);

        lock (_sendLock)
        {
            stream.Write(header, 0, HeaderSize);
            if (payload.Count > 0)
            {
                var buf = new byte[payload.Count];
                for (int i = 0; i < payload.Count; i++)
                {
                    buf[i] = payload[i];
                }

                stream.Write(buf, 0, buf.Length);
            }

            stream.Flush();
        }
    }

    // --------------------------------------------------------------- device ops

    /// <summary>Sets the given device into its Direct mode (per-LED control).</summary>
    public void SetDirectMode(int deviceIndex)
    {
        var dev = Devices.FirstOrDefault(d => d.Index == deviceIndex);
        if (dev is null)
        {
            return;
        }

        int modeIdx = dev.DirectModeIndex;
        if (modeIdx < 0)
        {
            return;
        }

        // Select the Direct mode, then activate custom (per-LED) control.
        SendSetMode(deviceIndex, (uint)modeIdx, dev.Modes[modeIdx]);
        SendPacket(PacketIds.SetCustomMode, (uint)deviceIndex, Array.Empty<byte>());
    }

    internal void SendSetMode(int deviceIndex, uint modeIndex, OpenRgbMode mode)
    {
        // rc3 wire layout (RGBController::GetModeDescription): a leading u32 total
        // payload size is REQUIRED - the server rejects the packet if the first
        // 4 bytes don't equal header.pkt_size. Protocol request is v4 (>= 3), so
        // the brightness block is present.
        var body = new List<byte>(128);
        ProtocolWriter.WriteU32(body, modeIndex);
        ProtocolWriter.WriteString(body, mode.Name);
        ProtocolWriter.WriteU32(body, mode.Value);
        ProtocolWriter.WriteU32(body, (uint)mode.Flags);
        ProtocolWriter.WriteU32(body, mode.SpeedMin);
        ProtocolWriter.WriteU32(body, mode.SpeedMax);
        ProtocolWriter.WriteU32(body, mode.BrightnessMin);
        ProtocolWriter.WriteU32(body, mode.BrightnessMax);
        ProtocolWriter.WriteU32(body, mode.ColorsMin);
        ProtocolWriter.WriteU32(body, mode.ColorsMax);
        ProtocolWriter.WriteU32(body, mode.SpeedValue);
        ProtocolWriter.WriteU32(body, mode.BrightnessValue);
        ProtocolWriter.WriteU32(body, (uint)mode.Direction);
        ProtocolWriter.WriteU32(body, (uint)mode.ColorMode);
        ProtocolWriter.WriteU16(body, 0); // mode color count - direct control supplies none

        var payload = new List<byte>(body.Count + 4);
        ProtocolWriter.WriteU32(payload, (uint)(body.Count + 4));
        payload.AddRange(body);

        SendPacket(PacketIds.UpdateMode, (uint)deviceIndex, payload);
    }

    /// <summary>
    /// Pushes per-LED colors to a device. <paramref name="rgb"/> holds 3 bytes per LED.
    /// Fire-and-forget; failures surface as a disconnect + reconnect.
    /// </summary>
    public void UpdateLeds(int deviceIndex, ReadOnlySpan<byte> rgb)
    {
        int ledCount = rgb.Length / 3;

        // rc3 wire layout (RGBController::GetColorDescription): [u32 total size]
        // [u16 led count][RGBA per led]. The server compares the first u32 against
        // header.pkt_size, so it must equal the whole payload length.
        var payload = new List<byte>(16 + rgb.Length);
        ProtocolWriter.WriteU32(payload, (uint)(4 + 2 + ledCount * 4));
        ProtocolWriter.WriteU16(payload, (ushort)ledCount);
        for (int i = 0; i < ledCount; i++)
        {
            payload.Add(rgb[i * 3]);
            payload.Add(rgb[i * 3 + 1]);
            payload.Add(rgb[i * 3 + 2]);
            payload.Add(0); // alpha
        }

        try
        {
            SendPacket(PacketIds.UpdateLeds, (uint)deviceIndex, payload);
        }
        catch
        {
            // reader thread will notice the dead socket
        }
    }

    // -------------------------------------------------------------------- parse

    private static OpenRgbDevice ParseDevice(int index, ProtocolReader r)
    {
        // The reply payload begins with a u32 data size (RGBController.cpp
        // GetDeviceDescription); skip it, the rest is the descriptor.
        r.ReadU32();

        var type = (DeviceType)r.ReadU32();
        string name = r.ReadString();
        string vendor = r.ReadString();
        string description = r.ReadString();
        string version = r.ReadString();
        string serial = r.ReadString();
        string location = r.ReadString();

        ushort modeCount = r.ReadU16();
        uint activeMode = r.ReadU32();

        var modes = new List<OpenRgbMode>(modeCount);
        for (int m = 0; m < modeCount; m++)
        {
            modes.Add(ParseMode(r));
        }

        ushort zoneCount = r.ReadU16();
        var zones = new List<OpenRgbZone>(zoneCount);
        int zoneStart = 0;
        for (int z = 0; z < zoneCount; z++)
        {
            // Zone wire layout (RGBController.cpp): name, type, leds_min, leds_max,
            // leds_count (there is NO start_idx field). proto>=4 then appends a
            // segment list after the (possibly empty) matrix.
            string zoneName = r.ReadString();
            var zoneType = (ZoneType)r.ReadU32();
            r.ReadU32(); // leds_min
            r.ReadU32(); // leds_max
            uint ledsCount = r.ReadU32();
            ushort matrixLen = r.ReadU16();
            if (matrixLen > 0)
            {
                uint matrixHeight = r.ReadU32();
                uint matrixWidth = r.ReadU32();
                for (int i = 0; i < matrixHeight * matrixWidth; i++)
                {
                    r.ReadU32(); // matrix map - unused
                }
            }

            ushort segmentCount = r.ReadU16();
            for (int s = 0; s < segmentCount; s++)
            {
                r.ReadString(); // segment name
                r.ReadU32(); // segment type
                r.ReadU32(); // segment start
                r.ReadU32(); // segment led count
            }

            zones.Add(new OpenRgbZone
            {
                Index = z,
                Name = zoneName,
                Type = zoneType,
                StartIndex = zoneStart,
                LedCount = (int)ledsCount,
            });
            zoneStart += (int)ledsCount;
        }

        ushort ledCount = r.ReadU16();
        var ledNames = new List<string>(ledCount);
        for (int l = 0; l < ledCount; l++)
        {
            ledNames.Add(r.ReadString());
            r.ReadU32(); // led value
        }

        ushort colorCount = r.ReadU16();
        for (int c = 0; c < colorCount; c++)
        {
            r.ReadU32(); // current colors - unused
        }

        return new OpenRgbDevice
        {
            Index = index,
            Name = name,
            Vendor = vendor,
            Description = description,
            Version = version,
            Serial = serial,
            Location = location,
            Type = type,
            Modes = modes,
            ActiveMode = (int)activeMode,
            Zones = zones,
            LedNames = ledNames,
        };
    }

    private static OpenRgbMode ParseMode(ProtocolReader r)
    {
        // rc3 mode wire order (proto>=3): name, value, flags, speed_min, speed_max,
        // brightness_min, brightness_max, colors_min, colors_max, speed, brightness,
        // direction, color_mode, u16 mode color count, colors.
        string name = r.ReadString();
        uint value = r.ReadU32();
        var flags = (ModeFlags)r.ReadU32();
        uint speedMin = r.ReadU32();
        uint speedMax = r.ReadU32();
        uint brightnessMin = r.ReadU32();
        uint brightnessMax = r.ReadU32();
        uint colorsMin = r.ReadU32();
        uint colorsMax = r.ReadU32();
        uint speedValue = r.ReadU32();
        uint brightnessValue = r.ReadU32();
        uint direction = r.ReadU32();
        var colorMode = (ColorMode)r.ReadU32();
        ushort modeColorCount = r.ReadU16();
        for (int c = 0; c < modeColorCount; c++)
        {
            r.ReadU32();
        }

        return new OpenRgbMode
        {
            Name = name,
            Value = value,
            Flags = flags,
            SpeedMin = speedMin,
            SpeedMax = speedMax,
            BrightnessMin = brightnessMin,
            BrightnessMax = brightnessMax,
            BrightnessValue = brightnessValue,
            ColorsMin = colorsMin,
            ColorsMax = colorsMax,
            SpeedValue = speedValue,
            Direction = (int)direction,
            ColorMode = colorMode,
        };
    }

    // --------------------------------------------------------------------- util

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

    private void CloseSocket()
    {
        try
        {
            _tcp?.Close();
        }
        catch
        {
        }

        _tcp = null;
        _stream = null;
    }

    private void SafeRaiseConnected()
    {
        try
        {
            Connected?.Invoke();
        }
        catch
        {
        }
    }

    private void SafeRaiseDisconnected()
    {
        try
        {
            Disconnected?.Invoke();
        }
        catch
        {
        }
    }

    private void SafeRaiseDevicesChanged()
    {
        try
        {
            DevicesChanged?.Invoke();
        }
        catch
        {
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts?.Cancel();
        CloseSocket();
        _requestLock.Dispose();
        _cts?.Dispose();
    }
}
