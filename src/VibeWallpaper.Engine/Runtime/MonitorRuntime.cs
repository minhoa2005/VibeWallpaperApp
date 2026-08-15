using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Runtime;

public sealed class MonitorRuntime
{
    private CancellationTokenSource? _transitionCancellation;
    private long _generation;

    public MonitorRuntime(MonitorIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        Identity = identity;
    }

    public MonitorIdentity Identity { get; }
    public nint HostHwnd { get; private set; }
    public DisplayViewport? Viewport { get; private set; }
    public IWallpaperRenderer? ActiveRenderer { get; internal set; }
    public long Generation => _generation;
    public AsyncCommitGate CommitGate { get; } = new();
    public PerformanceReasonSet Reasons { get; } = new();
    public Dictionary<long, IWallpaperRenderer> InFlightCandidates { get; } = [];

    internal void UpdateTarget(OutputAssignmentTarget target)
    {
        HostHwnd = target.HostHwnd;
        Viewport = target.Viewport;
    }

    internal (long Generation, CancellationToken Token) BeginTransition(
        OutputAssignmentTarget target,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
    {
        UpdateTarget(target);
        _transitionCancellation?.Cancel();
        _transitionCancellation?.Dispose();
        _transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, shutdownToken);
        return (++_generation, _transitionCancellation.Token);
    }

    internal void InvalidateTransition()
    {
        ++_generation;
        _transitionCancellation?.Cancel();
    }

    internal bool IsCurrent(long generation) => generation == _generation;
}
