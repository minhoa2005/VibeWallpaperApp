using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class DisplayGroupPlannerTests
{
    [Theory]
    [InlineData(DisplayMode.Duplicate)]
    [InlineData(DisplayMode.Span)]
    public void Plan_GroupedModes_CreateOneTargetPerLogicalCloneGroup(DisplayMode mode)
    {
        var a = Output("A", "clone-a", 0, 0, 1920, 1080);
        var clone = Output("A-PANEL", "clone-a", 0, 0, 1920, 1080);
        var b = Output("B", "clone-b", 1920, 0, 1920, 1080);
        var topology = new DisplayTopologySnapshot(4, new DisplayViewport(0, 0, 3840, 1080), [a, clone, b]);
        var definition = new DisplayGroupDefinition(GroupId, mode, Wallpaper.Id, [a.Descriptor.Identity, clone.Descriptor.Identity, b.Descriptor.Identity]);

        var plan = DisplayGroupPlanner.Plan(definition, Wallpaper, topology, [
            Binding(a, 101, FitMode.Contain, 24, 11),
            Binding(clone, 102, FitMode.Cover, 30, 22),
            Binding(b, 202, FitMode.Stretch, 60, 33)], topologyIsStable: true);

        Assert.Equal(2, plan.Request.Targets.Count);
        Assert.Equal(2, plan.RendererCount);
        Assert.Equal(2, plan.Request.Targets.Select(static target => target.HostHwnd).Distinct().Count());
        Assert.Equal([11, 33], plan.Request.Targets.Select(static target => target.Settings.VolumePercent));
    }

    [Fact]
    public void Plan_DisconnectedMemberIsPreservedAndExcludedUntilStableTopologyContainsIt()
    {
        var a = Output("A", "a", 0, 0, 1920, 1080);
        var disconnected = new MonitorIdentity("DISCONNECTED");
        var topology = new DisplayTopologySnapshot(8, a.Descriptor.Bounds, [a]);
        var definition = new DisplayGroupDefinition(GroupId, DisplayMode.Duplicate, Wallpaper.Id, [a.Descriptor.Identity, disconnected]);

        var plan = DisplayGroupPlanner.Plan(
            definition, Wallpaper, topology, [Binding(a, 101, FitMode.Cover, 30, 40)], topologyIsStable: true);

        Assert.Equal(definition.Members, plan.Definition.Members);
        Assert.Equal(disconnected, Assert.Single(plan.DisconnectedMembers));
        Assert.Single(plan.Request.Targets);
    }

    [Fact]
    public void Plan_UnstableTopologyDefersRecomputation()
    {
        var a = Output("A", "a", 0, 0, 1920, 1080);
        var topology = new DisplayTopologySnapshot(9, a.Descriptor.Bounds, [a]);
        var definition = new DisplayGroupDefinition(GroupId, DisplayMode.Duplicate, Wallpaper.Id, [a.Descriptor.Identity]);

        Assert.False(DisplayGroupPlanner.TryPlan(
            definition, Wallpaper, topology, [Binding(a, 101, FitMode.Cover, 30, 40)], topologyIsStable: false, out _));
    }

    [Fact]
    public void Plan_StableTopologyRejectsConnectedMemberWithoutHostBinding()
    {
        var a = Output("A", "a", 0, 0, 1920, 1080);
        var b = Output("B", "b", 1920, 0, 1920, 1080);
        var topology = new DisplayTopologySnapshot(10, new DisplayViewport(0, 0, 3840, 1080), [a, b]);
        var definition = new DisplayGroupDefinition(
            GroupId, DisplayMode.Duplicate, Wallpaper.Id, [a.Descriptor.Identity, b.Descriptor.Identity]);

        var error = Assert.Throws<ArgumentException>(() => DisplayGroupPlanner.Plan(
            definition, Wallpaper, topology, [Binding(a, 101, FitMode.Cover, 30, 40)], topologyIsStable: true));

        Assert.Contains("B", error.Message, StringComparison.Ordinal);
    }

    private static readonly DisplayGroupId GroupId = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly WallpaperDefinition Wallpaper = new(
        new WallpaperId(Guid.Parse("22222222-2222-2222-2222-222222222222")), "video",
        VideoSource.Create(Path.GetFullPath("group.mp4")), FitMode.Cover, 30, false, true, 40, false);

    private static DisplayGroupOutputBinding Binding(
        DisplayTopologyOutput output, nint host, FitMode fit, int fps, int volume) =>
        new(output, host, new OutputWallpaperSettings(fit, fps, volume));

    private static DisplayTopologyOutput Output(string key, string cloneKey, int x, int y, int width, int height)
    {
        var identity = new MonitorIdentity(key);
        var bounds = new DisplayViewport(x, y, width, height);
        var evidence = new MonitorIdentityEvidence(0, 0, 0, null, null, null, null, null, null, key, bounds);
        var descriptor = new MonitorDescriptor(identity, evidence, key, bounds, bounds, 96, 1, DisplayOrientation.Landscape, key == "A");
        return new DisplayTopologyOutput(descriptor, cloneKey, [evidence]);
    }
}
