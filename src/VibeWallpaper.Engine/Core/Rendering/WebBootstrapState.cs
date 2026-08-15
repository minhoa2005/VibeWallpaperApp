using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Core.Rendering;

public sealed record WebBootstrapState
{
    public WebBootstrapState(
        int schemaVersion,
        DisplayViewport virtualCanvas,
        DisplayViewport viewport,
        long monotonicTimeMilliseconds,
        uint deterministicSeed,
        int targetFps,
        string powerState)
    {
        if (schemaVersion != 1) throw new ArgumentOutOfRangeException(nameof(schemaVersion));
        ArgumentNullException.ThrowIfNull(virtualCanvas);
        ArgumentNullException.ThrowIfNull(viewport);
        if (monotonicTimeMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(monotonicTimeMilliseconds));
        if (targetFps is < 1 or > 60) throw new ArgumentOutOfRangeException(nameof(targetFps));
        if (string.IsNullOrWhiteSpace(powerState)) throw new ArgumentException("Power state is required.", nameof(powerState));
        SchemaVersion = schemaVersion;
        VirtualCanvas = virtualCanvas;
        Viewport = viewport;
        MonotonicTimeMilliseconds = monotonicTimeMilliseconds;
        DeterministicSeed = deterministicSeed;
        TargetFps = targetFps;
        PowerState = powerState.Trim();
    }

    public int SchemaVersion { get; }
    public DisplayViewport VirtualCanvas { get; }
    public DisplayViewport Viewport { get; }
    public long MonotonicTimeMilliseconds { get; }
    public uint DeterministicSeed { get; }
    public int TargetFps { get; }
    public string PowerState { get; }
}
