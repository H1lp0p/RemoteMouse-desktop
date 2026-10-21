using RemoteMouse.Core.SystemControl;
using Xunit;

namespace RemoteMouse.Core.Tests;

public class MediaTimelineTests
{
    [Fact]
    public void Seek_UsesTheSamePositionThePhoneWasShown()
    {
        var place = MediaTimeline.Describe(TimeSpan.Zero, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(30), null);
        Assert.Equal(30, place.PositionSeconds);
        Assert.Equal(180, place.DurationSeconds);
        Assert.Equal(TimeSpan.FromSeconds(40).Ticks, MediaTimeline.AbsoluteTicks(place, 10));
    }

    [Fact]
    public void Seek_StopsOneSecondBeforeTheEnd()
    {
        var place = MediaTimeline.Describe(TimeSpan.Zero, TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(170), null);
        Assert.Equal(179, MediaTimeline.TargetSeconds(place, 30));
        Assert.Equal(TimeSpan.FromSeconds(179).Ticks, MediaTimeline.AbsoluteTicks(place, 30));
    }

    [Fact]
    public void Seek_KeepsANonZeroTimelineOrigin()
    {
        var start = TimeSpan.FromSeconds(10);
        var place = MediaTimeline.Describe(start, TimeSpan.FromSeconds(190), TimeSpan.FromSeconds(40), null);
        Assert.Equal(30, place.PositionSeconds);
        Assert.Equal(TimeSpan.FromSeconds(50).Ticks, MediaTimeline.AbsoluteTicks(place, 10));
    }

    [Fact]
    public void AbsurdDuration_DoesNotBecomeTheSliderRange()
    {
        var place = MediaTimeline.Describe(TimeSpan.Zero, TimeSpan.FromDays(400), TimeSpan.FromSeconds(20), null);
        Assert.Equal(0, place.DurationSeconds);
        Assert.Equal(20, place.PositionSeconds);
        Assert.Equal(TimeSpan.FromSeconds(25).Ticks, MediaTimeline.AbsoluteTicks(place, 5));
    }
}
