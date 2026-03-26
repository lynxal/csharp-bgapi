using System.Buffers.Binary;

namespace SilabsBgapi.Protocol;

/// <summary>
/// BGAPI binary protocol header (4 bytes).
/// Byte 0: message type (bit 7) + technology (bits 3-6) + length high (bits 0-2)
/// Byte 1: length low
/// Byte 2: class index
/// Byte 3: command/event index
/// </summary>
public readonly struct BgapiHeader
{
    public const int Size = 4;
    public const byte CommandFlag = 0x20;
    public const byte EventFlag = 0xA0;

    public byte MessageType { get; }
    public ushort PayloadLength { get; }
    public byte ClassIndex { get; }
    public byte CommandIndex { get; }

    public bool IsEvent => (MessageType & 0x80) != 0;
    public bool IsResponse => !IsEvent;
    public byte DeviceId => (byte)((MessageType >> 3) & 0x0F);

    public BgapiHeader(byte messageType, ushort payloadLength, byte classIndex, byte commandIndex)
    {
        MessageType = messageType;
        PayloadLength = payloadLength;
        ClassIndex = classIndex;
        CommandIndex = commandIndex;
    }

    public static BgapiHeader CreateCommand(byte deviceId, byte classIndex, byte commandIndex, ushort payloadLength)
    {
        byte msgType = (byte)(CommandFlag | (deviceId << 3) | ((payloadLength >> 8) & 0x07));
        return new BgapiHeader(msgType, payloadLength, classIndex, commandIndex);
    }

    public static BgapiHeader Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size)
            throw new ArgumentException($"Header requires {Size} bytes, got {data.Length}");

        byte msgType = data[0];
        ushort length = (ushort)(((msgType & 0x07) << 8) | data[1]);
        return new BgapiHeader(msgType, length, data[2], data[3]);
    }

    public void WriteTo(Span<byte> destination)
    {
        if (destination.Length < Size)
            throw new ArgumentException($"Destination requires {Size} bytes");

        destination[0] = (byte)(MessageType | ((PayloadLength >> 8) & 0x07));
        destination[1] = (byte)(PayloadLength & 0xFF);
        destination[2] = ClassIndex;
        destination[3] = CommandIndex;
    }

    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        WriteTo(bytes);
        return bytes;
    }
}
