using System.Buffers.Binary;

namespace RemoteMouse.Core.Protocol;

/// <summary>
/// Little-endian realtime datagram on UDP 12346. Discovery on 12345 is not this format.
/// </summary>
public abstract record V2UdpFrame(ushort SessionId, uint Timestamp)
{
    public const int HeaderLength = 7;
    public const byte MouseMoveOpcode = 0x01;
    public const byte VolumeOpcode = 0x02;
    public const byte SeekOpcode = 0x03;
    public const int MouseMoveLength = 11;
    public const int VolumeLength = 8;
    public const int SeekLength = 9;

    public sealed record MouseMove(ushort SessionId, uint Timestamp, short Dx, short Dy) : V2UdpFrame(SessionId, Timestamp);

    public sealed record Volume(ushort SessionId, uint Timestamp, byte Level) : V2UdpFrame(SessionId, Timestamp);

    public sealed record Seek(ushort SessionId, uint Timestamp, ushort PositionSeconds) : V2UdpFrame(SessionId, Timestamp);

    public static bool TryParse(ReadOnlySpan<byte> payload, out V2UdpFrame? frame)
    {
        frame = null;
        if (payload.Length < HeaderLength) return false;

        var opcode = payload[0];
        var sessionId = BinaryPrimitives.ReadUInt16LittleEndian(payload[1..]);
        var timestamp = BinaryPrimitives.ReadUInt32LittleEndian(payload[3..]);
        switch (opcode)
        {
            case MouseMoveOpcode when payload.Length == MouseMoveLength:
                frame = new MouseMove(
                    sessionId,
                    timestamp,
                    BinaryPrimitives.ReadInt16LittleEndian(payload[7..]),
                    BinaryPrimitives.ReadInt16LittleEndian(payload[9..]));
                return true;
            case VolumeOpcode when payload.Length == VolumeLength:
                frame = new Volume(sessionId, timestamp, payload[7]);
                return true;
            case SeekOpcode when payload.Length == SeekLength:
                frame = new Seek(sessionId, timestamp, BinaryPrimitives.ReadUInt16LittleEndian(payload[7..]));
                return true;
            default:
                return false;
        }
    }
}

/// <summary>
/// A realtime datagram is accepted only for a live v2 WebSocket whose session id and peer IP both match.
/// Session id 0 is never issued. Closing the socket removes the only match, which kills the token.
/// </summary>
public static class V2Session
{
    public static bool AcceptsDatagram(ushort frameSessionId, string senderIp, ushort liveSessionId, string socketIp, int protocolVersion)
        => protocolVersion >= 2
           && liveSessionId != 0
           && frameSessionId == liveSessionId
           && string.Equals(senderIp, socketIp, StringComparison.Ordinal);
}
