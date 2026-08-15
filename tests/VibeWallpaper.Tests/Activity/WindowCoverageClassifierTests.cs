using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Tests.Activity;

public sealed class WindowCoverageClassifierTests
{
    [Fact]
    public void Classify_ExactPhysicalMonitorBounds_AreFullscreen()
    {
        var monitor = Monitor("primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040));

        var coverage = WindowCoverageClassifier.Classify(Window(101, 0, monitor.Bounds), monitor);

        Assert.Equal(CoverageKind.Fullscreen, coverage.Kind);
        Assert.Equal(1d, coverage.Fraction);
    }

    [Fact]
    public void Classify_ExactlyNinetyEightPercentWithTwoPixelUncoveredEdge_IsFullscreen()
    {
        var monitor = Monitor("threshold", new(0, 0, 100, 100));

        var coverage = WindowCoverageClassifier.Classify(Window(102, 0, new(2, 0, 98, 100)), monitor);

        Assert.Equal(CoverageKind.Fullscreen, coverage.Kind);
        Assert.Equal(0.98d, coverage.Fraction, 10);
    }

    [Fact]
    public void Classify_NinetySevenPointNinePercent_IsNotFullscreen()
    {
        var monitor = Monitor("below-threshold", new(0, 0, 1000, 1000));

        var coverage = WindowCoverageClassifier.Classify(Window(103, 0, new(0, 0, 979, 1000)), monitor);

        Assert.Equal(CoverageKind.Partial, coverage.Kind);
        Assert.Equal(0.979d, coverage.Fraction, 10);
    }

    [Fact]
    public void Classify_ExactBoundsAtNegativeCoordinates_AreFullscreen()
    {
        var monitor = Monitor("left", new(-1920, -120, 1920, 1080));

        var coverage = WindowCoverageClassifier.Classify(Window(104, 0, new(-1920, -120, 1920, 1080)), monitor);

        Assert.Equal(CoverageKind.Fullscreen, coverage.Kind);
    }

    [Fact]
    public void Classify_TaskbarVisibleMaximizedWindow_IsSeparateFromFullscreen()
    {
        var monitor = Monitor("primary", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040));

        var coverage = WindowCoverageClassifier.Classify(Window(105, 0, monitor.WorkArea), monitor);

        Assert.Equal(CoverageKind.MaximizedWorkArea, coverage.Kind);
        Assert.Equal(1040d / 1080d, coverage.Fraction, 10);
    }

    [Fact]
    public void Classify_BorderlessStyleWithoutCoveringGeometry_IsOnlyPartial()
    {
        var monitor = Monitor("primary", new(0, 0, 1920, 1080));

        var coverage = WindowCoverageClassifier.Classify(Window(106, 0, new(200, 100, 1200, 800)), monitor);

        Assert.Equal(CoverageKind.Partial, coverage.Kind);
    }

    [Fact]
    public void Classify_FullscreenOnSecondary_CoversOnlySecondary()
    {
        var primary = Monitor("primary", new(0, 0, 1920, 1080));
        var secondary = Monitor("secondary", new(1920, 0, 2560, 1440));
        var foreground = Window(201, 0, new(100, 100, 800, 600));
        var secondaryFullscreen = Window(202, 1, secondary.Bounds);

        var coverage = WindowCoverageClassifier.Classify(
            [foreground, secondaryFullscreen],
            [primary, secondary]);

        Assert.Collection(
            coverage,
            item =>
            {
                Assert.Equal(primary.Identity, item.Monitor);
                Assert.Equal(201, item.Hwnd);
                Assert.Equal(CoverageKind.Partial, item.Kind);
            },
            item =>
            {
                Assert.Equal(secondary.Identity, item.Monitor);
                Assert.Equal(202, item.Hwnd);
                Assert.Equal(CoverageKind.Fullscreen, item.Kind);
            });
    }

    [Fact]
    public void Classify_WindowSpanningWithoutCoveringEitherOutput_IsPartialOnBoth()
    {
        var left = Monitor("left", new(0, 0, 1000, 1000));
        var right = Monitor("right", new(1000, 0, 1000, 1000));
        var spanning = Window(301, 0, new(500, 0, 1000, 1000));

        var coverage = WindowCoverageClassifier.Classify([spanning], [left, right]);

        Assert.All(coverage, item =>
        {
            Assert.Equal(CoverageKind.Partial, item.Kind);
            Assert.Equal(0.5d, item.Fraction, 10);
        });
    }

    [Fact]
    public void Classify_FullscreenBehindTopmostRelevantWindow_DoesNotCoverOutput()
    {
        var monitor = Monitor("primary", new(0, 0, 1920, 1080));
        var topmost = Window(401, 0, new(100, 100, 800, 600));
        var staleFullscreen = Window(402, 1, monitor.Bounds);

        var coverage = Assert.Single(WindowCoverageClassifier.Classify([staleFullscreen, topmost], [monitor]));

        Assert.Equal(401, coverage.Hwnd);
        Assert.Equal(CoverageKind.Partial, coverage.Kind);
    }

    private static WindowSnapshot Window(nint hwnd, int zOrder, DisplayViewport bounds) =>
        new(hwnd, hwnd, 42, zOrder, bounds, true, false, false, false, false, false);

    private static MonitorDescriptor Monitor(
        string key,
        DisplayViewport bounds,
        DisplayViewport? workArea = null)
    {
        var identity = new MonitorIdentity(key);
        var evidence = new MonitorIdentityEvidence(
            1,
            0,
            0,
            null,
            null,
            $"path-{key}",
            null,
            null,
            null,
            key,
            bounds);
        return new MonitorDescriptor(
            identity,
            evidence,
            key,
            bounds,
            workArea ?? bounds,
            96,
            1,
            DisplayOrientation.Landscape,
            key == "primary");
    }
}
