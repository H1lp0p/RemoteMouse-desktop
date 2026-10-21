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

    void Trust(string? clientName);
    void Log(string message);
    Task PublishAsync(ServerEvent evt, Audience audience, CancellationToken cancellationToken);
}
