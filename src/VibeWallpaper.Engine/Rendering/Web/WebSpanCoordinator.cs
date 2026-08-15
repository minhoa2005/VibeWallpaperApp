using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Rendering.Web;

public static class WebSpanCoordinator
{
    public static IReadOnlyList<WebBootstrapState> CreateStates(
        SpanLayout layout,
        int targetFps,
        string powerState,
        long? nowMilliseconds = null,
        uint? seed = null)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (targetFps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(targetFps));
        ArgumentException.ThrowIfNullOrWhiteSpace(powerState);
        var timestamp = nowMilliseconds ?? Environment.TickCount64;
        var deterministicSeed = seed ?? (uint)HashCode.Combine(layout.VirtualCanvas.X, layout.VirtualCanvas.Y, layout.VirtualCanvas.Width, layout.VirtualCanvas.Height);
        return layout.Viewports
            .Select(viewport => new WebBootstrapState(
                1,
                layout.VirtualCanvas,
                viewport.OutputViewport,
                timestamp,
                deterministicSeed,
                targetFps,
                powerState))
            .ToArray();
    }
}
