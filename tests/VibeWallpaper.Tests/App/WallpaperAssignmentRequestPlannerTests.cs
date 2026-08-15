using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Tests.App;

public sealed class WallpaperAssignmentRequestPlannerTests
{
    [Fact]
    public void Plan_Independent_CreatesSingleUngroupedTarget()
    {
        var topology = Topology();

        var request = WallpaperAssignmentRequestPlanner.Plan(
            Wallpaper, DisplayMode.Independent, [A], topology,
            new Dictionary<string, nint>(StringComparer.Ordinal) { [A.Key] = 101 });

        Assert.Equal(DisplayMode.Independent, request.Mode);
        Assert.Null(request.GroupId);
        var target = Assert.Single(request.Targets);
        Assert.Equal(A, target.Monitor);
        Assert.Equal((nint)101, target.HostHwnd);
    }

    [Theory]
    [InlineData(DisplayMode.Duplicate)]
    [InlineData(DisplayMode.Span)]
    public void Plan_GroupedMode_CreatesOneRendererTargetPerOutput(DisplayMode mode)
    {
        var topology = Topology();

        var request = WallpaperAssignmentRequestPlanner.Plan(
            Wallpaper, mode, [A, B], topology,
            new Dictionary<string, nint>(StringComparer.Ordinal) { [A.Key] = 101, [B.Key] = 202 });

        Assert.Equal(mode, request.Mode);
        Assert.NotNull(request.GroupId);
        Assert.Equal([A, B], request.Targets.Select(static target => target.Monitor));
        if (mode == DisplayMode.Span)
        {
            Assert.Equal(2, request.Targets.Select(static target => target.SourceCrop).Distinct().Count());
        }
    }

    [Fact]
    public void Plan_GroupedMode_PreservesExistingGroupAndPerOutputSettings()
    {
        var topology = Topology();
        var group = DisplayGroupId.New();
        var settings = new Dictionary<string, OutputWallpaperSettings>(StringComparer.Ordinal)
        {
            [A.Key] = new(FitMode.Contain, 24, 0),
            [B.Key] = new(FitMode.Stretch, 60, 0),
        };

        var request = WallpaperAssignmentRequestPlanner.Plan(
            Wallpaper, DisplayMode.Span, [A, B], topology,
            new Dictionary<string, nint>(StringComparer.Ordinal) { [A.Key] = 101, [B.Key] = 202 },
            group,
            settings);

        Assert.Equal(group, request.GroupId);
        Assert.Equal(FitMode.Contain, request.Targets[0].Settings.Fit);
        Assert.Equal(24, request.Targets[0].Settings.TargetFps);
        Assert.Equal(FitMode.Stretch, request.Targets[1].Settings.Fit);
        Assert.Equal(60, request.Targets[1].Settings.TargetFps);
    }

    [Theory]
    [InlineData(DisplayMode.Duplicate)]
    [InlineData(DisplayMode.Span)]
    public void Plan_GroupedMode_RejectsFewerThanTwoOutputs(DisplayMode mode)
    {
        Assert.Throws<ArgumentException>(() => WallpaperAssignmentRequestPlanner.Plan(
            Wallpaper, mode, [A], Topology(),
            new Dictionary<string, nint>(StringComparer.Ordinal) { [A.Key] = 101 }));
    }

    private static readonly MonitorIdentity A = new("DISPLAY-A");
    private static readonly MonitorIdentity B = new("DISPLAY-B");
    private static readonly WallpaperDefinition Wallpaper = new(
        WallpaperId.New(), "Grouped", SolidColorSource.Create("#123456"),
        FitMode.Cover, 30, false, false, 0, false);

    private static DisplayTopologySnapshot Topology()
    {
        var first = Output(A, 0);
        var second = Output(B, 1920);
        return new DisplayTopologySnapshot(
            1, new DisplayViewport(0, 0, 3840, 1080), [first, second]);
    }

    private static DisplayTopologyOutput Output(MonitorIdentity identity, int x)
    {
        var bounds = new DisplayViewport(x, 0, 1920, 1080);
        var evidence = new MonitorIdentityEvidence(
            0, 0, 0, null, null, null, null, null, null, identity.Key, bounds);
        var descriptor = new MonitorDescriptor(
            identity, evidence, identity.Key, bounds, bounds, 96, 1,
            DisplayOrientation.Landscape, x == 0);
        return new DisplayTopologyOutput(descriptor, identity.Key, [evidence]);
    }
}
