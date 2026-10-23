using RemoteMouse.Core.Protocol;

namespace RemoteMouse.Core.Input;

/// <summary>
/// v2 mouse samples, keyed by the phone's monotonic timestamp.
/// The playhead runs <see cref="BaseDelayMs"/> behind the newest sample and at most <see cref="MaxDelayMs"/> when arrival jitter is larger.
/// v1 <see cref="CursorInterpolator"/> is not used here.
/// </summary>
public sealed class MouseTimeline
{
    public const int BaseDelayMs = 8;
    public const int MaxDelayMs = 20;
    public const int BarrierTimeoutMs = 30;

    private readonly object _gate = new();
    private readonly List<Sample> _pending = new();
    private bool _started;
    private bool _haveNewest;
    private bool _hasBarrier;
    private uint _playhead;
    private uint _newest;
    private uint _barrier;
    private uint _lastNewestPhone;
    private long _lastNewestLocal = long.MinValue;
    private long _anchorLocal;
    private uint _anchorPlayhead;
    private int _delayMs = BaseDelayMs;

    public void AddSample(uint timestamp, int dx, int dy, long localNowMs, Action<int, int> emit)
    {
        lock (_gate)
        {
            foreach (var step in AddUnlocked(timestamp, dx, dy, localNowMs))
                emit(step.Dx, step.Dy);
        }
    }

    public void Pump(long localNowMs, Action<int, int> emit)
    {
        lock (_gate)
        {
            foreach (var step in AdvanceUnlocked(localNowMs, ceiling: null))
                emit(step.Dx, step.Dy);
        }
    }

    /// <summary>
    /// Play samples up to <paramref name="inputTime"/>, then run <paramref name="command"/>.
    /// If nothing at or after that timestamp has arrived, wait at most <see cref="BarrierTimeoutMs"/>.
    /// On timeout the command runs at the cursor the playhead has already rendered, the playhead jumps to
    /// <paramref name="inputTime"/>, and a later sample that is not newer than that barrier is dropped.
    /// </summary>
    public async Task AwaitClickAsync(
        uint inputTime,
        Func<long> clock,
        Action<int, int> emit,
        Action command,
        CancellationToken cancellationToken,
        Func<int, CancellationToken, Task>? wait = null)
    {
        wait ??= static (milliseconds, token) => Task.Delay(milliseconds, token);
        var start = clock();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool done;
            lock (_gate)
                done = TryFinishClick(inputTime, clock(), start, emit, command);
            if (done) return;
            await wait(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool TryFinishClick(uint inputTime, long now, long start, Action<int, int> emit, Action command)
    {
        if (Covers(inputTime))
        {
            var resumeAt = TimeBased(now);
            Emit(AdvanceUnlocked(now, inputTime), emit);
            Emit(TakeUpTo(inputTime), emit);
            RaiseBarrier(inputTime, now);
            command();
            if (_started && PhoneTime.IsNewer(resumeAt, _playhead))
            {
                SetPlayhead(resumeAt, now);
                Emit(ConsumeDue(), emit);
            }
            return true;
        }

        Emit(AdvanceUnlocked(now, ceiling: null), emit);
        if (now - start < BarrierTimeoutMs)
            return false;

        DropUpTo(inputTime);
        RaiseBarrier(inputTime, now);
        command();
        return true;
    }

    private List<Step> AddUnlocked(uint timestamp, int dx, int dy, long localNowMs)
    {
        var steps = new List<Step>();
        if (_started)
            steps.AddRange(AdvanceUnlocked(localNowMs, ceiling: null));

        if (_hasBarrier && !PhoneTime.IsNewer(timestamp, _barrier))
            return steps;

        if (_started && PhoneTime.Delta(timestamp, _playhead) < 0)
        {
            NoteLate(timestamp, localNowMs);
            return steps;
        }

        var sample = new Sample(timestamp, dx, dy);
        if (!_haveNewest)
        {
            BeginNewest(timestamp, localNowMs);
            _pending.Add(sample);
            steps.AddRange(ConsumeDue());
            return steps;
        }

        Insert(sample);
        if (PhoneTime.IsNewer(timestamp, _newest))
        {
            NoteNewest(timestamp, localNowMs);
            steps.AddRange(ConsumeDue());
        }
        else if (!PhoneTime.IsNewer(timestamp, _playhead))
        {
            steps.AddRange(ConsumeDue());
        }

        return steps;
    }

    private void BeginNewest(uint timestamp, long localNowMs)
    {
        _haveNewest = true;
        _newest = timestamp;
        _lastNewestPhone = timestamp;
        _lastNewestLocal = localNowMs;
        _delayMs = BaseDelayMs;
        if (!_started)
        {
            SetPlayhead(PhoneTime.Add(timestamp, -_delayMs), localNowMs);
            return;
        }

        var lag = PhoneTime.Delta(timestamp, _playhead);
        var desired = PhoneTime.Add(timestamp, -_delayMs);
        if (lag > MaxDelayMs && PhoneTime.IsNewer(desired, _playhead))
            SetPlayhead(desired, localNowMs);
    }

    private void NoteNewest(uint timestamp, long localNowMs)
    {
        var phoneDelta = PhoneTime.Delta(timestamp, _lastNewestPhone);
        var localDelta = (int)Math.Clamp(localNowMs - _lastNewestLocal, -120_000, 120_000);
        var lateBy = phoneDelta > 0 ? localDelta - phoneDelta : 0;
        var grew = false;
        if (lateBy > _delayMs)
        {
            _delayMs = Math.Min(MaxDelayMs, lateBy);
            grew = true;
        }
        else if (phoneDelta > 0 && lateBy <= 0 && _delayMs > BaseDelayMs)
        {
            _delayMs--;
        }

        _newest = timestamp;
        _lastNewestPhone = timestamp;
        _lastNewestLocal = localNowMs;

        var desired = PhoneTime.Add(_newest, -_delayMs);
        var lag = PhoneTime.Delta(_newest, _playhead);
        if (lag > MaxDelayMs)
        {
            if (PhoneTime.IsNewer(desired, _playhead))
                SetPlayhead(desired, localNowMs);
        }
        else if (grew && PhoneTime.Delta(desired, _playhead) < 0)
        {
            SetPlayhead(desired, localNowMs);
        }
    }

    private void NoteLate(uint timestamp, long localNowMs)
    {
        if (!_haveNewest) return;
        var behindMs = -PhoneTime.Delta(timestamp, _playhead);
        if (behindMs <= _delayMs) return;
        _delayMs = Math.Min(MaxDelayMs, behindMs);
        var desired = PhoneTime.Add(_newest, -_delayMs);
        if (PhoneTime.Delta(desired, _playhead) < 0)
            SetPlayhead(desired, localNowMs);
    }

    private List<Step> AdvanceUnlocked(long localNowMs, uint? ceiling)
    {
        if (!_started) return new List<Step>();
        var elapsed = localNowMs - _anchorLocal;
        if (elapsed <= 0) return new List<Step>();
        if (elapsed > int.MaxValue) elapsed = int.MaxValue;
        var next = PhoneTime.Add(_anchorPlayhead, (int)elapsed);
        if (ceiling is uint cap && PhoneTime.IsNewer(next, cap))
            next = cap;
        if (!PhoneTime.IsNewer(next, _playhead))
        {
            _anchorLocal = localNowMs;
            _anchorPlayhead = _playhead;
            return new List<Step>();
        }

        _playhead = next;
        _anchorLocal = localNowMs;
        _anchorPlayhead = next;
        return ConsumeDue();
    }

    private uint TimeBased(long localNowMs)
    {
        if (!_started) return _playhead;
        var elapsed = localNowMs - _anchorLocal;
        if (elapsed <= 0) return _playhead;
        if (elapsed > int.MaxValue) elapsed = int.MaxValue;
        return PhoneTime.Add(_anchorPlayhead, (int)elapsed);
    }

    private bool Covers(uint inputTime)
        => _haveNewest && PhoneTime.Delta(_newest, inputTime) >= 0;

    private List<Step> TakeUpTo(uint inclusive)
    {
        var steps = new List<Step>();
        while (_pending.Count > 0 && !PhoneTime.IsNewer(_pending[0].Timestamp, inclusive))
        {
            var sample = _pending[0];
            _pending.RemoveAt(0);
            if (sample.Dx != 0 || sample.Dy != 0)
                steps.Add(new Step(sample.Dx, sample.Dy));
        }
        return steps;
    }

    private void DropUpTo(uint inclusive)
    {
        while (_pending.Count > 0 && !PhoneTime.IsNewer(_pending[0].Timestamp, inclusive))
            _pending.RemoveAt(0);
    }

    private void RaiseBarrier(uint inputTime, long localNowMs)
    {
        if (!_hasBarrier || PhoneTime.IsNewer(inputTime, _barrier))
        {
            _barrier = inputTime;
            _hasBarrier = true;
        }

        if (!_started || PhoneTime.IsNewer(inputTime, _playhead))
            SetPlayhead(inputTime, localNowMs);
        else
        {
            _anchorLocal = localNowMs;
            _anchorPlayhead = _playhead;
        }
    }

    private void SetPlayhead(uint value, long localNowMs)
    {
        if (_hasBarrier && PhoneTime.Delta(value, _barrier) < 0)
            value = _barrier;
        _playhead = value;
        _anchorLocal = localNowMs;
        _anchorPlayhead = value;
        _started = true;
    }

    private List<Step> ConsumeDue()
    {
        var steps = new List<Step>();
        while (_pending.Count > 0 && !PhoneTime.IsNewer(_pending[0].Timestamp, _playhead))
        {
            var sample = _pending[0];
            _pending.RemoveAt(0);
            if (sample.Dx != 0 || sample.Dy != 0)
                steps.Add(new Step(sample.Dx, sample.Dy));
        }
        return steps;
    }

    private void Insert(Sample sample)
    {
        var index = _pending.Count;
        for (var i = 0; i < _pending.Count; i++)
        {
            if (PhoneTime.Delta(_pending[i].Timestamp, sample.Timestamp) > 0)
            {
                index = i;
                break;
            }
        }
        _pending.Insert(index, sample);
    }

    private static void Emit(List<Step> steps, Action<int, int> emit)
    {
        foreach (var step in steps)
            emit(step.Dx, step.Dy);
    }

    private readonly record struct Sample(uint Timestamp, int Dx, int Dy);

    private readonly record struct Step(int Dx, int Dy);
}
