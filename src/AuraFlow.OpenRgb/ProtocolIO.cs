using System.Buffers.Binary;
using System.Text;

namespace AuraFlow.OpenRgb;

/// <summary>Big buffer reader for OpenRGB wire format (little-endian, length-prefixed ASCII strings).</summary>
internal sealed class ProtocolReader
{
    private readonly byte[] _buffer;
    private int _pos;

    public ProtocolReader(byte[] buffer)
    {
        _buffer = buffer;
        _pos = 0;
    }

    public int Remaining => _buffer.Length - _pos;

    public uint ReadU32()
    {
        uint v = BinaryPrimitives.ReadUInt32LittleEndian(_buffer.AsSpan(_pos, 4));
        _pos += 4;
        return v;
    }

    public ushort ReadU16()
    {
        ushort v = BinaryPrimitives.ReadUInt16LittleEndian(_buffer.AsSpan(_pos, 2));
        _pos += 2;
        return v;
    }

    public string ReadString()
    {
        ushort len = ReadU16();
        if (len == 0 || Remaining < len)
        {
            if (len > 0)
            {
                _pos += len;
            }

            return string.Empty;
        }

        // Length includes the null terminator - strip it.
        int contentLen = len;
        if (_buffer[_pos + len - 1] == 0)
        {
            contentLen--;
        }

        string s = Encoding.ASCII.GetString(_buffer, _pos, contentLen);
        _pos += len;
        return s;
    }
}

/// <summary>Writer for OpenRGB wire format payloads.</summary>
internal static class ProtocolWriter
{
    public static void WriteU32(List<byte> list, uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, value);
        list.AddRange(b.ToArray());
    }

    public static void WriteString(List<byte> list, string value)
    {
        // OpenRGB wire format: u16 length INCLUDING the null terminator, then
        // length bytes where the last one is '\0'.
        byte[] ascii = Encoding.ASCII.GetBytes(value);
        WriteU16(list, (ushort)(ascii.Length + 1));
        list.AddRange(ascii);
        list.Add(0);
    }

    public static void WriteU16(List<byte> list, ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, value);
        list.AddRange(b.ToArray());
    }
}
