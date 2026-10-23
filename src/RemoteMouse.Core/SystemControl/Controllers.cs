namespace RemoteMouse.Core.SystemControl;

public interface IMouseController
{
    void Move(int dx, int dy);
    void Button(string button, string action);
    void Scroll(int deltaX, int deltaY);
}

public interface IKeyboardController
{
    void Chord(IReadOnlyList<int> virtualKeys);
}

public readonly record struct VolumeState(int Level, bool Muted);

public interface IVolumeController
{
    VolumeState Get();
    VolumeState SetLevel(int level);
    VolumeState Step(int delta);
    VolumeState SetMute(bool mute);
}

public sealed record MediaSnapshot(
    string? Title,
    string? Artist,
    int PositionSeconds,
    int DurationSeconds,
    bool IsPlaying,
    bool CanSeek);

public interface IMediaController
{
    /// <summary>Raised when playback changes outside a command from the phone.</summary>
    event Action? Changed;

    MediaSnapshot Get();
    MediaSnapshot PlayPause();
    MediaSnapshot Next();
    MediaSnapshot Previous();
    MediaSnapshot Seek(int seconds);

    /// <summary>Absolute position in the same seconds the phone shows. Not a relative skip.</summary>
    MediaSnapshot SeekAbsolute(int positionSeconds);
}

public interface ITextController
{
    /// <summary>Raised when the focused field changes outside a command from the phone.</summary>
    event Action<string>? Changed;

    string Get();
    string Set(string text);
}

public sealed class MemoryVolumeController : IVolumeController
{
    private readonly object _gate = new();
    private int _level = 50;
    private bool _muted;

    public VolumeState Get()
    {
        lock (_gate) return new VolumeState(_level, _muted);
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
            _muted = mute;
            return new VolumeState(_level, _muted);
        }
    }

    private VolumeState Apply(int level, bool clearMute)
    {
        lock (_gate)
        {
            _level = level;
            if (clearMute) _muted = false;
            return new VolumeState(_level, _muted);
        }
    }
}

/// <summary>
/// Stand-in media session until SMTC is wired behind <see cref="IMediaController"/>.
/// The phone Player still receives the same media_update fields.
/// </summary>
public sealed class MemoryMediaController : IMediaController
{
#pragma warning disable CS0067
    public event Action? Changed;
#pragma warning restore CS0067

    private readonly object _gate = new();
    private string? _title = "[Youtube] \"I created the most strange android app\"";
    private string? _artist = "Code-bullet";
    private int _duration = 2595;
    private int _anchorPos = 839;
    private DateTime _anchorAt = DateTime.UtcNow;
    private bool _playing = true;
    private readonly bool _canSeek = true;

    public MediaSnapshot Get()
    {
        lock (_gate) return Snapshot(EffectiveLocked());
    }

    public MediaSnapshot PlayPause()
    {
        lock (_gate)
        {
            Commit(null, !_playing);
            return Snapshot(EffectiveLocked());
        }
    }

    public MediaSnapshot Next()
    {
        lock (_gate)
        {
            _title = "[Youtube] \"Next track from stub\"";
            _artist = "Stub Artist";
            _duration = 2595;
            Commit(0, true);
            return Snapshot(EffectiveLocked());
        }
    }

    public MediaSnapshot Previous()
    {
        lock (_gate)
        {
            _title = "[Youtube] \"I created the most strange android app\"";
            _artist = "Code-bullet";
            _duration = 2595;
            Commit(0, true);
            return Snapshot(EffectiveLocked());
        }
    }

    public MediaSnapshot Seek(int seconds)
    {
        lock (_gate)
        {
            Commit(EffectiveLocked() + seconds, null);
            return Snapshot(EffectiveLocked());
        }
    }

    public MediaSnapshot SeekAbsolute(int positionSeconds)
    {
        lock (_gate)
        {
            var place = new MediaTimeline.Placement(0, _duration, TimeSpan.Zero);
            Commit(MediaTimeline.AbsoluteTargetSeconds(place, positionSeconds), null);
            return Snapshot(EffectiveLocked());
        }
    }

    private int EffectiveLocked()
    {
        if (!_playing) return _anchorPos;
        var elapsed = (int)(DateTime.UtcNow - _anchorAt).TotalSeconds;
        var pos = Math.Clamp(_anchorPos + elapsed, 0, _duration);
        if (pos >= _duration)
        {
            Commit(_duration, false);
            return _duration;
        }
        return pos;
    }

    private void Commit(int? position, bool? playing)
    {
        var pos = position ?? EffectivePositionOnly();
        _anchorPos = Math.Clamp(pos, 0, _duration);
        _anchorAt = DateTime.UtcNow;
        if (playing is { } next) _playing = next;
        if (_playing && _anchorPos >= _duration) _playing = false;
    }

    private int EffectivePositionOnly()
    {
        if (!_playing) return _anchorPos;
        var elapsed = (int)(DateTime.UtcNow - _anchorAt).TotalSeconds;
        return Math.Clamp(_anchorPos + elapsed, 0, _duration);
    }

    private MediaSnapshot Snapshot(int position) => new(_title, _artist, position, _duration, _playing, _canSeek);
}

public sealed class MemoryTextController : ITextController
{
#pragma warning disable CS0067
    public event Action<string>? Changed;
#pragma warning restore CS0067

    private readonly object _gate = new();
    private string _text = "";

    public string Get()
    {
        lock (_gate) return _text;
    }

    public string Set(string text)
    {
        lock (_gate)
        {
            _text = text ?? "";
            return _text;
        }
    }
}
