using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Tests.Core;

public sealed class SpanLayoutCalculatorTests
{
    [Fact]
    public void Calculate_UsesPhysicalBoundsAcrossNegativeCoordinatesPortraitAndGaps()
    {
        var left = Monitor("LEFT", -1200, -200, 1200, 1920, 144, DisplayOrientation.Portrait, false);
        var right = Monitor("RIGHT", 200, 0, 2560, 1440, 96, DisplayOrientation.Landscape, true);

        var layout = SpanLayoutCalculator.Calculate([left, right]);

        Assert.Equal(new DisplayViewport(-1200, -200, 3960, 1920), layout.VirtualCanvas);
        var leftView = Assert.Single(layout.Viewports, viewport => viewport.Monitor == left.Identity);
        Assert.Equal(left.Bounds, leftView.OutputViewport);
        Assert.Equal(new NormalizedSourceRect(0, 0, 1200d / 3960d, 1), leftView.SourceCrop);
        var rightView = Assert.Single(layout.Viewports, viewport => viewport.Monitor == right.Identity);
        Assert.Equal(new NormalizedSourceRect(1400d / 3960d, 200d / 1920d, 2560d / 3960d, 1440d / 1920d), rightView.SourceCrop);
    }

    [Fact]
    public void Calculate_PrimaryChangeDoesNotChangePhysicalSpan()
    {
        var first = new[]
        {
            Monitor("A", 0, 0, 1920, 1080, 96, DisplayOrientation.Landscape, true),
            Monitor("B", 1920, 0, 1920, 1080, 192, DisplayOrientation.Landscape, false),
        };
        var second = new[]
        {
            Monitor("A", 0, 0, 1920, 1080, 96, DisplayOrientation.Landscape, false),
            Monitor("B", 1920, 0, 1920, 1080, 192, DisplayOrientation.Landscape, true),
        };

        Assert.Equal(SpanLayoutCalculator.Calculate(first), SpanLayoutCalculator.Calculate(second));
    }

    [Theory]
    [InlineData(-0.01, 0, 1, 1)]
    [InlineData(0, 0, 0, 1)]
    [InlineData(0.5, 0, 0.6, 1)]
    [InlineData(0, 0.5, 1, 0.6)]
    public void NormalizedSourceRect_RejectsCropOutsideUnitSquare(double x, double y, double width, double height) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NormalizedSourceRect(x, y, width, height));

    private static MonitorDescriptor Monitor(
        string key, int x, int y, int width, int height, uint dpi, DisplayOrientation orientation, bool primary)
    {
        var identity = new MonitorIdentity(key);
        var bounds = new DisplayViewport(x, y, width, height);
        var evidence = new MonitorIdentityEvidence(0, 0, 0, null, null, null, null, null, null, key, bounds);
        return new MonitorDescriptor(identity, evidence, key, bounds, bounds, dpi, dpi / 96d, orientation, primary);
    }
}
