using VibeWallpaper.App.ViewModels;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Tests.App;

public sealed class MonitorTopologyLayoutTests
{
    [Fact]
    public void Arrange_PreservesNegativeCoordinatesAndOrientation()
    {
        var left = Descriptor("left", new DisplayViewport(-1080, 0, 1080, 1920));
        var primary = Descriptor("primary", new DisplayViewport(0, 0, 2560, 1440));

        var result = MonitorTopologyLayout.Arrange([left, primary], 720, 300, 16);

        Assert.True(result[left.Identity].Left < result[primary.Identity].Left);
        Assert.True(result[left.Identity].Height > result[left.Identity].Width);
        Assert.All(result.Values, r => Assert.True(r.Left >= 16 && r.Top >= 16));
    }

    private static MonitorDescriptor Descriptor(string key, DisplayViewport bounds) => new(
        new MonitorIdentity(key),
        new MonitorIdentityEvidence(1, 1, 1, null, null, $"DISPLAY#{key}", "ACM", 1, 1, key, bounds),
        key, bounds, bounds, 96, 1, bounds.Height > bounds.Width ? DisplayOrientation.Portrait : DisplayOrientation.Landscape, key == "primary");
}
