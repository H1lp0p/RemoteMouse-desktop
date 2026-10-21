namespace RemoteMouse.Core.Input;

/// <summary>
/// Follows the queued cursor delta with a short exponential glide.
/// Packets are not finished on a hard deadline: a late tick cannot dump the whole queue,
/// and the integers emitted over time, plus <see cref="Flush"/>, add up to the deltas that were added.
/// </summary>
public sealed class CursorInterpolator
{
    /// <summary>How quickly the pointer catches the queue. Larger feels smoother and a little later.</summary>
    public const double TauMs = 28;

    /// <summary>A stalled wake applies at most this much of the curve, then the rest waits for later ticks.</summary>
    public const double MaxStepMs = 16;

    private double _x;
    private double _y;
    private double _fracX;
    private double _fracY;
    private double _lastMs;
    private bool _active;

    public bool IsActive => _active;

    public void Add(int dx, int dy, double nowMs)
    {
        if (dx == 0 && dy == 0) return;
        if (!_active) _lastMs = nowMs;
        _x += dx;
        _y += dy;
        _active = true;
    }

    public (int X, int Y) Advance(double nowMs)
    {
        if (!_active) return (0, 0);

        var dt = nowMs - _lastMs;
        if (dt < 0) dt = 0;
        if (dt > MaxStepMs) dt = MaxStepMs;
        _lastMs = nowMs;

        var ix = 0;
        var iy = 0;
        if (dt > 0)
        {
            var alpha = 1 - Math.Exp(-dt / TauMs);
            ix = Integrate(ref _x, ref _fracX, alpha);
            iy = Integrate(ref _y, ref _fracY, alpha);
        }

        if (Math.Abs(_x) < 0.05 && Math.Abs(_y) < 0.05)
        {
            ix += Close(ref _x, ref _fracX);
            iy += Close(ref _y, ref _fracY);
            _active = false;
        }

        return (ix, iy);
    }

    public (int X, int Y) Flush()
    {
        _active = false;
        return (Close(ref _x, ref _fracX), Close(ref _y, ref _fracY));
    }

    private static int Integrate(ref double pending, ref double frac, double alpha)
    {
        var take = pending * alpha;
        pending -= take;
        frac += take;
        var pixels = (int)Math.Truncate(frac);
        frac -= pixels;
        return pixels;
    }

    private static int Close(ref double pending, ref double frac)
    {
        var pixels = (int)Math.Round(pending + frac, MidpointRounding.AwayFromZero);
        pending = 0;
        frac = 0;
        return pixels;
    }
}
