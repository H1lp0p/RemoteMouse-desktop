using RemoteMouse.Core.SystemControl;

namespace RemoteMouse.Core.Protocol;

public interface ISessionContext
{
    string DeviceId { get; }
    string DeviceName { get; }
    bool MediaSubscribed { get; set; }
    bool TextSubscribed { get; set; }

    IMouseController Mouse { get; }
    IKeyboardController Keyboard { get; }
    IVolumeController Volume { get; }
    IMediaController Media { get; }
    ITextController Text { get; }

    /// <summary>1 keeps the JSON path. 2 is the UDP realtime session negotiated by /pair.</summary>
    int ProtocolVersion { get; }

    /// <summary>
    /// v2 click/scroll fence. The command runs after samples up to <paramref name="inputTime"/> are played,
    /// or after the 30 ms wait. v1 handlers do not call this.
    /// </summary>
    Task SyncPointerAsync(uint inputTime, Action command, CancellationToken cancellationToken);

    void Trust(string? clientName);
    void Log(string message);
    Task PublishAsync(ServerEvent evt, Audience audience, CancellationToken cancellationToken);
}
