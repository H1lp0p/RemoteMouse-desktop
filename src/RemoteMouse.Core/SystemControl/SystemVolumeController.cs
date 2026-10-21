using NAudio.CoreAudioApi;

namespace RemoteMouse.Core.SystemControl;

/// <summary>
/// Master volume of the default render device. Falls back to an in-memory level if WASAPI is unavailable.
/// </summary>
public sealed class SystemVolumeController : IVolumeController, IDisposable
{
    private readonly MemoryVolumeController _memory = new();
    private readonly Action<string>? _log;
    private readonly object _gate = new();
    private bool _loggedFailure;

    public SystemVolumeController(Action<string>? log = null) => _log = log;

    public VolumeState Get()
    {
        lock (_gate)
            return Read(Role.Console) ?? Read(Role.Multimedia) ?? _memory.Get();
    }

    public VolumeState SetLevel(int level) => Apply(Math.Clamp(level, 0, 100), clearMute: true);

    public VolumeState Step(int delta)
    {
        var current = Get();
        return Apply(Math.Clamp(current.Level + delta, 0, 100), clearMute: false);
    }

    public VolumeState SetMute(bool mute)
    {
        lock (_gate)
        {
            var wrote = TryMute(Role.Console, mute) | TryMute(Role.Multimedia, mute);
            if (!wrote) return _memory.SetMute(mute);
            return Read(Role.Console) ?? Read(Role.Multimedia) ?? new VolumeState(_memory.Get().Level, mute);
        }
    }

    public void Dispose()
    {
    }

    private VolumeState Apply(int level, bool clearMute)
    {
        lock (_gate)
        {
            // Each call opens the endpoint on this thread. A cached device dies after the
            // default output changes and then the setter throws, so the phone was echoing a fake level.
            var wrote = TryWrite(Role.Console, level, clearMute) | TryWrite(Role.Multimedia, level, clearMute);
            if (!wrote)
                return clearMute ? _memory.SetLevel(level) : _memory.Step(level - _memory.Get().Level);
            return Read(Role.Console) ?? Read(Role.Multimedia) ?? new VolumeState(level, false);
        }
    }

    private bool TryWrite(Role role, int level, bool clearMute)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            var volume = device.AudioEndpointVolume;
            if (clearMute) volume.Mute = false;
            volume.MasterVolumeLevelScalar = Math.Clamp(level, 0, 100) / 100f;
            return true;
        }
        catch (Exception ex)
        {
            LogOnce("volume set " + role + ": " + ex.Message);
            return false;
        }
    }

    private bool TryMute(Role role, bool mute)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            device.AudioEndpointVolume.Mute = mute;
            return true;
        }
        catch (Exception ex)
        {
            LogOnce("volume mute " + role + ": " + ex.Message);
            return false;
        }
    }

    private VolumeState? Read(Role role)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, role);
            var volume = device.AudioEndpointVolume;
            return new VolumeState((int)Math.Round(volume.MasterVolumeLevelScalar * 100), volume.Mute);
        }
        catch (Exception ex)
        {
            LogOnce("volume read " + role + ": " + ex.Message);
            return null;
        }
    }

    private void LogOnce(string message)
    {
        if (_loggedFailure) return;
        _loggedFailure = true;
        _log?.Invoke(message);
    }
}
