namespace RemoteMouse.Core.Protocol;

/// <summary>
/// Semantic command from the phone. Codecs produce these; handlers never see the wire format.
/// </summary>
public abstract record ClientAction
{
    public sealed record Heartbeat(long? Timestamp) : ClientAction;
    public sealed record Save(string? ClientName) : ClientAction;
    public sealed record MouseMove(int Dx, int Dy) : ClientAction;

    /// <summary><paramref name="InputTime"/> is the phone clock. Null means v1: run immediately.</summary>
    public sealed record MouseClick(string Button, string ActionName, uint? InputTime = null) : ClientAction;

    /// <summary><paramref name="InputTime"/> is the phone clock. Null means v1: run immediately.</summary>
    public sealed record MouseScroll(int DeltaX, int DeltaY, uint? InputTime = null) : ClientAction;
    public sealed record KeySequence(IReadOnlyList<int> Keys) : ClientAction;
    public sealed record TextSubscribe() : ClientAction;
    public sealed record TextUnsubscribe() : ClientAction;
    public sealed record TextSet(string Text) : ClientAction;
    public sealed record VolumeSet(int Level) : ClientAction;
    public sealed record VolumeStep(string Direction) : ClientAction;
    public sealed record VolumeMute(bool Mute) : ClientAction;
    public sealed record MediaSubscribe() : ClientAction;
    public sealed record MediaUnsubscribe() : ClientAction;
    public sealed record MediaPlayPause() : ClientAction;
    public sealed record MediaNext() : ClientAction;
    public sealed record MediaPrev() : ClientAction;
    public sealed record MediaSeek(int Seconds) : ClientAction;

    /// <summary>A frame this product does not know yet. Ignored so a newer phone does not drop the socket.</summary>
    public sealed record Unrecognized(string? Type) : ClientAction;
}

/// <summary>
/// Semantic reply. Each codec decides how to write it, including future binary frames.
/// </summary>
public abstract record ServerEvent
{
    public sealed record HeartbeatAck(long? Timestamp) : ServerEvent;
    public sealed record SaveSuccess(string DeviceId, string DeviceName) : ServerEvent;
    public sealed record VolumeUpdate(int Level, bool Muted) : ServerEvent;
    public sealed record MediaUpdate(
        string? Title,
        string? Artist,
        int PositionSeconds,
        int DurationSeconds,
        bool IsPlaying,
        bool CanSeek) : ServerEvent;
    public sealed record TextUpdate(string Text) : ServerEvent;
    public sealed record ContextChange(string Context) : ServerEvent;
    public sealed record Error(int Code, string Message) : ServerEvent;
}

public enum Audience
{
    Self,
    Everyone,
    MediaSubscribers,
    TextSubscribers
}

public sealed class WireFormatException : Exception
{
    public WireFormatException(string message) : base(message) { }
}
