namespace RemoteMouse.Core.SystemControl;

/// <summary>
/// Turns an SMTC timeline into the position the phone shows, and into an absolute seek that uses the same numbers.
/// Landing on the exact end of a track makes players skip to the next item, so a seek that would pass the end stops one second short.
/// </summary>
public static class MediaTimeline
{
    public readonly record struct Placement(int PositionSeconds, int DurationSeconds, TimeSpan Origin);

    public static Placement Describe(TimeSpan start, TimeSpan end, TimeSpan position, TimeSpan? playingAhead)
    {
        var ahead = playingAhead is TimeSpan extra && extra > TimeSpan.Zero && extra < TimeSpan.FromSeconds(15)
            ? extra
            : TimeSpan.Zero;
        var duration = end - start;
        var relative = position - start + ahead;
        var origin = start;

        if (!Plausible(duration))
        {
            if (Plausible(end) && position >= TimeSpan.Zero && position <= end)
            {
                duration = end;
                relative = position + ahead;
                origin = TimeSpan.Zero;
            }
            else
            {
                duration = TimeSpan.Zero;
                origin = TimeSpan.Zero;
                relative = position + ahead;
            }
        }

        if (relative < TimeSpan.Zero) relative = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && relative > duration) relative = duration;
        if (!Plausible(relative) && duration <= TimeSpan.Zero) relative = TimeSpan.Zero;
        return new Placement(Seconds(relative), Seconds(duration), origin);
    }

    public static int TargetSeconds(Placement current, int deltaSeconds)
    {
        var target = current.PositionSeconds + deltaSeconds;
        if (target < 0) target = 0;
        if (current.DurationSeconds > 5 && target >= current.DurationSeconds)
            target = current.DurationSeconds - 1;
        else if (current.DurationSeconds > 0 && target > current.DurationSeconds)
            target = current.DurationSeconds;
        return target;
    }

    public static long AbsoluteTicks(Placement current, int deltaSeconds)
    {
        var target = TargetSeconds(current, deltaSeconds);
        var absolute = current.Origin + TimeSpan.FromSeconds(target);
        if (absolute < TimeSpan.Zero) absolute = TimeSpan.Zero;
        return absolute.Ticks;
    }

    /// <summary>Absolute position on the same scale as <see cref="Placement.PositionSeconds"/>.</summary>
    public static int AbsoluteTargetSeconds(Placement current, int positionSeconds)
    {
        var target = positionSeconds;
        if (target < 0) target = 0;
        if (current.DurationSeconds > 5 && target >= current.DurationSeconds)
            target = current.DurationSeconds - 1;
        else if (current.DurationSeconds > 0 && target > current.DurationSeconds)
            target = current.DurationSeconds;
        return target;
    }

    public static long AbsolutePositionTicks(Placement current, int positionSeconds)
    {
        var target = AbsoluteTargetSeconds(current, positionSeconds);
        var absolute = current.Origin + TimeSpan.FromSeconds(target);
        if (absolute < TimeSpan.Zero) absolute = TimeSpan.Zero;
        return absolute.Ticks;
    }

    private static bool Plausible(TimeSpan value)
        => value > TimeSpan.Zero && value < TimeSpan.FromHours(8);

    private static int Seconds(TimeSpan value)
        => (int)Math.Clamp(value.TotalSeconds, 0, int.MaxValue);
}
