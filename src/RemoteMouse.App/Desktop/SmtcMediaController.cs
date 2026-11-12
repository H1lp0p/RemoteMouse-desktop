using Windows.Media.Control;
using RemoteMouse.Core.Input;
using RemoteMouse.Core.SystemControl;

namespace RemoteMouse.App.Desktop;

/// <summary>
/// Reads and controls the current Windows media session (SMTC): the track that is actually playing.
/// </summary>
public sealed class SmtcMediaController : IMediaController, IDisposable
{
    private readonly Action<string> _log;
    private readonly object _gate = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private MediaSnapshot _last = Empty;
    private long _quietUntil;
    private int _started;
    private int _disposed;

    public SmtcMediaController(Action<string> log) => _log = log;

    public event Action? Changed;

    public MediaSnapshot Get() => Run(ReadAsync);

    public MediaSnapshot PlayPause()
    {
        Quiet();
        return Run(async () =>
        {
            var session = await SessionAsync();
            var accepted = session is not null && await TryTransportAsync(session);
            if (!accepted)
            {
                // Chrome reports the YouTube session but rejects SMTC transport commands.
                // The hardware media key still reaches the browser.
                Tap(VkMediaPlayPause);
                _log("media play/pause via media key");
                await Task.Delay(200);
            }
            else
            {
                await Task.Delay(60);
            }
            return await ReadAsync();
        });
    }

    public MediaSnapshot Next()
    {
        Quiet();
        return Run(async () =>
        {
            var before = _last;
            var session = await SessionAsync();
            var accepted = false;
            if (session is not null)
            {
                var controls = session.GetPlaybackInfo().Controls;
                accepted = controls.IsNextEnabled && await session.TrySkipNextAsync();
            }
            if (!accepted)
            {
                Tap(VkMediaNext);
                _log("media next via media key");
                await Task.Delay(200);
            }
            else
            {
                await Task.Delay(80);
            }
            return await ReadAfterTrackChange(before);
        });
    }

    public MediaSnapshot Previous()
    {
        Quiet();
        return Run(async () =>
        {
            var before = _last;
            var session = await SessionAsync();
            var accepted = false;
            if (session is not null)
            {
                var controls = session.GetPlaybackInfo().Controls;
                accepted = controls.IsPreviousEnabled && await session.TrySkipPreviousAsync();
            }
            if (!accepted)
            {
                Tap(VkMediaPrevious);
                _log("media previous via media key");
                await Task.Delay(200);
            }
            else
            {
                await Task.Delay(80);
            }
            return await ReadAfterTrackChange(before);
        });
    }

    public MediaSnapshot Seek(int seconds)
    {
        Quiet();
        return Run(async () =>
        {
            var session = await SessionAsync();
            var accepted = false;
            MediaSnapshot? sought = null;
            if (session is not null && seconds != 0)
                (accepted, sought) = await TrySeekAsync(session, seconds);
            if (!accepted && seconds != 0 && Math.Abs(seconds) <= 12)
            {
                if (YoutubeKeySeek.Try(seconds, out var detail))
                    _log("media seek via keyboard (" + detail + ")");
                else
                    _log("media seek not accepted (" + detail + ")");
                await Task.Delay(120);
            }
            else if (!accepted && seconds != 0)
            {
                _log("media seek rejected by session (" + seconds + "s)");
            }
            var read = await ReadAsync();
            return sought is MediaSnapshot target ? read with { PositionSeconds = target.PositionSeconds, DurationSeconds = target.DurationSeconds } : read;
        });
    }

    public MediaSnapshot SeekAbsolute(int positionSeconds)
    {
        Quiet();
        return Run(async () =>
        {
            var session = await SessionAsync();
            MediaSnapshot? sought = null;
            if (session is not null)
                sought = await TrySeekAbsoluteAsync(session, positionSeconds);
            var read = await ReadAsync();
            return sought is MediaSnapshot target ? read with { PositionSeconds = target.PositionSeconds, DurationSeconds = target.DurationSeconds } : read;
        });
    }

    private const ushort VkMediaNext = 0xB0;
    private const ushort VkMediaPrevious = 0xB1;
    private const ushort VkMediaPlayPause = 0xB3;

    private readonly SendInputKeyboard _keys = new();

    private void Tap(ushort virtualKey) => _keys.Chord(new[] { (int)virtualKey });

    private static async Task<bool> TryTransportAsync(GlobalSystemMediaTransportControlsSession session)
    {
        var info = session.GetPlaybackInfo();
        var controls = info.Controls;
        var playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing && controls.IsPauseEnabled && await session.TryPauseAsync()) return true;
        if (!playing && controls.IsPlayEnabled && await session.TryPlayAsync()) return true;
        if (controls.IsPlayPauseToggleEnabled && await session.TryTogglePlayPauseAsync()) return true;
        return false;
    }

    private async Task<(bool Accepted, MediaSnapshot? Target)> TrySeekAsync(GlobalSystemMediaTransportControlsSession session, int seconds)
    {
        var timeline = session.GetTimelineProperties();
        var place = MediaTimeline.Describe(timeline.StartTime, timeline.EndTime, timeline.Position, PlayingAhead(session, timeline));
        var targetSeconds = MediaTimeline.TargetSeconds(place, seconds);
        if (targetSeconds == place.PositionSeconds && Math.Abs(seconds) >= 1)
            return (false, null);
        var accepted = await session.TryChangePlaybackPositionAsync(MediaTimeline.AbsoluteTicks(place, seconds));
        if (!accepted) return (false, null);
        Quiet(800);
        var read = _last;
        return (true, read with { PositionSeconds = targetSeconds, DurationSeconds = place.DurationSeconds > 0 ? place.DurationSeconds : read.DurationSeconds });
    }

    private async Task<MediaSnapshot?> TrySeekAbsoluteAsync(GlobalSystemMediaTransportControlsSession session, int positionSeconds)
    {
        var timeline = session.GetTimelineProperties();
        var place = MediaTimeline.Describe(timeline.StartTime, timeline.EndTime, timeline.Position, PlayingAhead(session, timeline));
        var targetSeconds = MediaTimeline.AbsoluteTargetSeconds(place, positionSeconds);
        var accepted = await session.TryChangePlaybackPositionAsync(MediaTimeline.AbsolutePositionTicks(place, positionSeconds));
        if (!accepted) return null;
        Quiet(800);
        var read = _last;
        return read with { PositionSeconds = targetSeconds, DurationSeconds = place.DurationSeconds > 0 ? place.DurationSeconds : read.DurationSeconds };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        // Unhook off the UI thread, then stay alive briefly so Windows can drop the
        // SMTC listener while this process still exists. Exiting with it attached crashes Chrome.
        Task.Run(() =>
        {
            lock (_gate)
                ReleaseSession();
        }).GetAwaiter().GetResult();
        Thread.Sleep(200);
    }

    private void ReleaseSession()
    {
        try
        {
            Unhook(_session);
        }
        catch (Exception ex)
        {
            _log("media unhook: " + ex.Message);
        }
        _session = null;
        if (_manager is null) return;
        try
        {
            _manager.CurrentSessionChanged -= OnSessionChanged;
        }
        catch (Exception ex)
        {
            _log("media unhook: " + ex.Message);
        }
        _manager = null;
    }

    private static MediaSnapshot Empty => new(null, null, 0, 0, false, false);

    private void Quiet(int milliseconds = 350) => _quietUntil = Environment.TickCount64 + milliseconds;

    /// <summary>
    /// SMTC often reports the previous title for a few hundred milliseconds after a skip.
    /// Wait until the title or artist changes, then return that read. A restart of the same
    /// track does not change them, so the wait is capped.
    /// </summary>
    private async Task<MediaSnapshot> ReadAfterTrackChange(MediaSnapshot before)
    {
        var deadline = Environment.TickCount64 + 400;
        var snapshot = await ReadAsync();
        while (Environment.TickCount64 < deadline && SameTrack(before, snapshot))
        {
            await Task.Delay(40);
            snapshot = await ReadAsync();
        }
        return snapshot;
    }

    private static bool SameTrack(MediaSnapshot previous, MediaSnapshot next)
        => previous.Title == next.Title && previous.Artist == next.Artist;

    private MediaSnapshot Run(Func<Task<MediaSnapshot>> action)
    {
        try
        {
            return Task.Run(action).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log("media: " + ex.Message);
            return _last;
        }
    }

    private async Task<GlobalSystemMediaTransportControlsSession?> SessionAsync()
    {
        if (Volatile.Read(ref _disposed) == 1) return null;
        var manager = _manager;
        if (manager is null)
        {
            manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            lock (_gate)
            {
                if (Volatile.Read(ref _disposed) == 1) return null;
                if (_manager is null)
                {
                    _manager = manager;
                    _manager.CurrentSessionChanged += OnSessionChanged;
                }
                else
                {
                    manager = _manager;
                }
            }
        }

        var session = manager.GetCurrentSession();
        Hook(session);
        return session;
    }

    private async Task<MediaSnapshot> ReadAsync()
    {
        var session = await SessionAsync();
        if (session is null)
        {
            _last = Empty;
            return _last;
        }

        string? title = null;
        string? artist = null;
        try
        {
            var props = await session.TryGetMediaPropertiesAsync();
            if (props is not null)
            {
                title = string.IsNullOrWhiteSpace(props.Title) ? null : props.Title;
                artist = string.IsNullOrWhiteSpace(props.Artist) ? null : props.Artist;
            }
        }
        catch (Exception ex)
        {
            _log("media metadata: " + ex.Message);
        }

        var playback = session.GetPlaybackInfo();
        var timeline = session.GetTimelineProperties();
        var place = MediaTimeline.Describe(
            timeline.StartTime,
            timeline.EndTime,
            timeline.Position,
            PlayingAhead(session, timeline));

        var snapshot = new MediaSnapshot(
            title,
            artist,
            place.PositionSeconds,
            place.DurationSeconds,
            playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
            playback.Controls.IsPlaybackPositionEnabled || place.DurationSeconds > 0);
        _last = snapshot;
        return snapshot;
    }

    private void Hook(GlobalSystemMediaTransportControlsSession? session)
    {
        lock (_gate)
        {
            if (Volatile.Read(ref _disposed) == 1) return;
            if (ReferenceEquals(_session, session)) return;
            Unhook(_session);
            _session = session;
            if (session is null) return;
            session.MediaPropertiesChanged += OnSessionUpdated;
            session.PlaybackInfoChanged += OnSessionUpdated;
            session.TimelinePropertiesChanged += OnSessionUpdated;
            if (Interlocked.Exchange(ref _started, 1) == 0)
                _log("SMTC media session attached");
        }
    }

    private void Unhook(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null) return;
        session.MediaPropertiesChanged -= OnSessionUpdated;
        session.PlaybackInfoChanged -= OnSessionUpdated;
        session.TimelinePropertiesChanged -= OnSessionUpdated;
    }

    private void OnSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
        => PublishExternal();

    private void OnSessionUpdated(GlobalSystemMediaTransportControlsSession sender, object args)
        => PublishExternal();

    private int _publishing;
    private int _deferredPublish;

    private void PublishExternal()
    {
        if (Volatile.Read(ref _disposed) == 1) return;
        var now = Environment.TickCount64;
        if (now < _quietUntil)
        {
            // The skip's metadata event arrives inside the quiet window. Dropping it left the
            // phone on the previous track; read once more when the window ends.
            if (Interlocked.Exchange(ref _deferredPublish, 1) == 1) return;
            var waitMs = (int)Math.Clamp(_quietUntil - now + 30, 30, 2_000);
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(waitMs);
                }
                finally
                {
                    Interlocked.Exchange(ref _deferredPublish, 0);
                }
                PublishExternal();
            });
            return;
        }
        if (Interlocked.Exchange(ref _publishing, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200);
                var previous = _last;
                var snapshot = await ReadAsync();
                if (Meaningful(previous, snapshot))
                    Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _log("media watch: " + ex.Message);
            }
            finally
            {
                Interlocked.Exchange(ref _publishing, 0);
            }
        });
    }

    private static bool Meaningful(MediaSnapshot previous, MediaSnapshot next)
        => previous.Title != next.Title
           || previous.Artist != next.Artist
           || previous.IsPlaying != next.IsPlaying
           || previous.CanSeek != next.CanSeek
           || previous.DurationSeconds != next.DurationSeconds
           || Math.Abs(previous.PositionSeconds - next.PositionSeconds) >= 2;

    private static TimeSpan? PlayingAhead(
        GlobalSystemMediaTransportControlsSession session,
        GlobalSystemMediaTransportControlsSessionTimelineProperties timeline)
    {
        if (session.GetPlaybackInfo().PlaybackStatus != GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            return null;
        var updated = timeline.LastUpdatedTime;
        if (updated.Year < 2000) return null;
        var elapsed = DateTimeOffset.UtcNow - updated;
        if (elapsed <= TimeSpan.Zero || elapsed >= TimeSpan.FromSeconds(15)) return null;
        return elapsed;
    }
}
