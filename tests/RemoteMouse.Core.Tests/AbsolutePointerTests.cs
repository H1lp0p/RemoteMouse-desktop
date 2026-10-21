using RemoteMouse.Core.Input;
using Xunit;

namespace RemoteMouse.Core.Tests;

public class AbsolutePointerTests
{
    [Theory]
    [InlineData(1920, 1080, 0, 0)]
    [InlineData(2560, 1440, 0, 0)]
    [InlineData(3840, 1080, -1920, 0)]
    [InlineData(1920, 2160, 0, -1080)]
    [InlineData(1366, 768, 0, 0)]
    [InlineData(1536, 864, -1536, -864)]
    public void EveryPixel_RoundTripsAcrossTheVirtualDesktop(int width, int height, int originX, int originY)
    {
        for (var x = 0; x < width; x++)
        {
            var absolute = AbsolutePointer.Normalize(originX + x, originY, originX, originY, width, height);
            var pixel = AbsolutePointer.ToPixel(absolute.X, absolute.Y, originX, originY, width, height);
            Assert.Equal(originX + x, pixel.X);
            Assert.InRange(absolute.X, 0, 65535);
        }

        for (var y = 0; y < height; y++)
        {
            var absolute = AbsolutePointer.Normalize(originX, originY + y, originX, originY, width, height);
            var pixel = AbsolutePointer.ToPixel(absolute.X, absolute.Y, originX, originY, width, height);
            Assert.Equal(originY + y, pixel.Y);
            Assert.InRange(absolute.Y, 0, 65535);
        }
    }

    [Fact]
    public void LeftMonitor_StaysLeftOfThePrimary()
    {
        var onLeft = AbsolutePointer.Normalize(-1920, 100, -1920, 0, 3840, 1080);
        var onPrimary = AbsolutePointer.Normalize(0, 100, -1920, 0, 3840, 1080);
        Assert.True(onLeft.X < onPrimary.X);
        Assert.Equal(-1920, AbsolutePointer.ToPixel(onLeft.X, onLeft.Y, -1920, 0, 3840, 1080).X);
        Assert.Equal(0, AbsolutePointer.ToPixel(onPrimary.X, onPrimary.Y, -1920, 0, 3840, 1080).X);
    }
}
