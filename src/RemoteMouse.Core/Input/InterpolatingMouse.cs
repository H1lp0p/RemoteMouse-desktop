using System.Diagnostics;
using System.Runtime.InteropServices;

namespace RemoteMouse.Core.Input;

/// <summary>
/// Applies mouse deltas through <see cref="CursorInterpolator"/> so the pointer glides
/// across several frames instead of jumping once per batch. Clicks and scrolls flush the path first.
/// </summary>
public sealed class InterpolatingMouse : SystemControl.IMouseController, IDisposable
{
    private readonly SendInputMouse _sink = new();
    private readonly CursorInterpolator _path = new();
    private readonly object _gate = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;

    public InterpolatingMouse()
    {
        timeBeginPeriod(1);
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "RemoteMouse cursor"
        };
        _thread.Start();
    }

    public void Move(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;
        lock (_gate) _path.Add(dx, dy, Now());
        _wake.Set();
    }

    /// <summary>v2 playhead output. Does not enter <see cref="CursorInterpolator"/>.</summary>
    public void MoveImmediate(int dx, int dy) => _sink.Move(dx, dy);

    public void Button(string button, string action)
    {
        Flush();
        _sink.Button(button, action);
    }

    public void Scroll(int deltaX, int deltaY)
    {
        Flush();
        _sink.Scroll(deltaX, deltaY);
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(TimeSpan.FromSeconds(1));
        timeEndPeriod(1);
        _wake.Dispose();
    }

    private void Loop()
    {
        while (!_stop)
        {
            bool active;
            lock (_gate) active = _path.IsActive;
            if (!active)
            {
                _wake.WaitOne(100);
                continue;
            }

            (int X, int Y) step;
            lock (_gate) step = _path.Advance(Now());
            if (step.X != 0 || step.Y != 0)
                _sink.Move(step.X, step.Y);
            Thread.Sleep(1);
        }
    }

    private void Flush()
    {
        (int X, int Y) step;
        lock (_gate) step = _path.Flush();
        if (step.X != 0 || step.Y != 0)
            _sink.Move(step.X, step.Y);
    }

    private static double Now() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint period);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint period);
}
