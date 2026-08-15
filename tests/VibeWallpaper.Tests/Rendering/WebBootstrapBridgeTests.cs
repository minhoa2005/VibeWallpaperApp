using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Rendering.Web;

namespace VibeWallpaper.Tests.Rendering;

public sealed class WebBootstrapBridgeTests
{
    [Fact]
    public void SpanStates_ShareCanvasClockAndSeed_ButUseDistinctViewports()
    {
        var a = new MonitorIdentity("A");
        var b = new MonitorIdentity("B");
        var canvas = new DisplayViewport(0, 0, 3840, 1080);
        var layout = new SpanLayout(canvas, [
            new SpanViewport(a, canvas, new DisplayViewport(0, 0, 1920, 1080), new NormalizedSourceRect(0, 0, .5, 1)),
            new SpanViewport(b, canvas, new DisplayViewport(1920, 0, 1920, 1080), new NormalizedSourceRect(.5, 0, .5, 1)),
        ]);

        var states = WebSpanCoordinator.CreateStates(layout, 30, "ac", nowMilliseconds: 1234, seed: 42);

        Assert.Equal(2, states.Count);
        Assert.All(states, state => Assert.Equal(canvas, state.VirtualCanvas));
        Assert.Single(states.Select(state => state.DeterministicSeed).Distinct());
        Assert.Single(states.Select(state => state.MonotonicTimeMilliseconds).Distinct());
        Assert.Equal(2, states.Select(state => state.Viewport).Distinct().Count());
    }

    [Fact]
    public void BootstrapState_RejectsInvalidVersionAndFps()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebBootstrapState(0, new DisplayViewport(0, 0, 1, 1), new DisplayViewport(0, 0, 1, 1), 0, 1, 30, "ac"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WebBootstrapState(1, new DisplayViewport(0, 0, 1, 1), new DisplayViewport(0, 0, 1, 1), 0, 1, 61, "ac"));
    }
}
