using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Tests.Core;

public sealed class RendererPerformanceContractTests
{
    [Fact]
    public void Request_RejectsCadenceOutsideThrottledState()
    {
        Assert.Throws<ArgumentException>(() =>
            new RendererPerformanceRequest(PerformanceState.Running, 30));
    }

    [Fact]
    public void Capabilities_ExposeCurrentBackendTruth()
    {
        Assert.Equal(
            new RendererCapabilities(true, true, false, false, false),
            RendererCapabilities.LibVlcFallback);
    }
}
