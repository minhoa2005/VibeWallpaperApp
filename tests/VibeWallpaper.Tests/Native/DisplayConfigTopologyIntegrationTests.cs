using VibeWallpaper.Engine.Monitors;

namespace VibeWallpaper.Tests.Native;

public sealed class DisplayConfigTopologyIntegrationTests
{
    [Fact]
    [Trait("Category", "WindowsIntegration")]
    public void Capture_OnInteractiveDesktop_ReturnsStablePhysicalTopology()
    {
        var service = new DisplayConfigTopologyService();
        if (!service.IsInteractiveDesktopAvailable)
        {
            Assert.Skip("No interactive Windows desktop is available to enumerate displays.");
        }

        var first = service.Capture();
        var second = service.Capture();

        Assert.NotEmpty(first.LogicalOutputs);
        Assert.All(first.LogicalOutputs, output =>
        {
            Assert.True(output.Descriptor.Bounds.Width > 0);
            Assert.True(output.Descriptor.Bounds.Height > 0);
            Assert.True(output.Descriptor.Dpi >= 96);
        });
        Assert.Equal(
            first.LogicalOutputs.Count,
            first.LogicalOutputs.Select(output => output.Descriptor.Identity.Key).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            first.LogicalOutputs.Select(output => output.Descriptor.Identity.Key).Order(StringComparer.Ordinal),
            second.LogicalOutputs.Select(output => output.Descriptor.Identity.Key).Order(StringComparer.Ordinal));
    }
}
