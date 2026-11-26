using System.Buffers.Binary;

namespace CreatureIA.Protocol;

public static class FrameCodec
{
    public static byte[] Encode(MessageType type, ReadOnlySpan<byte> payload)
    {
        if (!Enum.IsDefined(type)) throw new ArgumentOutOfRangeException(nameof(type));
        var bodyLength = checked(payload.Length + 1);
        if (bodyLength > ProtocolConstants.MaxBodyLength) throw new InvalidDataException("Frame exceeds maximum body length.");
        var frame = new byte[checked(4 + bodyLength)];
        BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)bodyLength);
        frame[4] = (byte)type;
        payload.CopyTo(frame.AsSpan(5));
        return frame;
    }

    public static ProtocolFrame Decode(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 5) throw new InvalidDataException("Frame is truncated.");
        var bodyLength = BinaryPrimitives.ReadUInt32LittleEndian(frame);
        if (bodyLength is 0 or > ProtocolConstants.MaxBodyLength) throw new InvalidDataException("Invalid frame body length.");
        if (frame.Length != checked(4 + (int)bodyLength)) throw new InvalidDataException("Frame length does not match prefix.");
        var type = (MessageType)frame[4];
        if (!Enum.IsDefined(type)) throw new InvalidDataException("Unknown message type.");
        return new ProtocolFrame(type, frame[5..].ToArray());
    }
}
