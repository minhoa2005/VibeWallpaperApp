namespace VibeWallpaper.Engine.Core.Rendering;

public enum RendererLifecycle
{
    Created,
    Initializing,
    Loading,
    Ready,
    Active,
    Stopped,
    Faulted,
    Disposed,
}

public enum PerformanceState
{
    Running,
    Throttled,
    Suspended,
}

public sealed record RendererCapabilities(
    bool CanPauseDecode,
    bool CanSuspendPresentation,
    bool CanThrottlePresentation,
    bool CanShareDecode,
    bool UsesHardwareDecode)
{
    public static RendererCapabilities Solid { get; } = new(false, true, false, false, false);
    public static RendererCapabilities Web { get; } = new(true, true, true, false, true);
    public static RendererCapabilities LibVlcFallback { get; } = new(true, true, false, false, false);
}

public sealed record RendererPerformanceRequest
{
    public RendererPerformanceRequest(PerformanceState state, int? targetPresentationFps = null)
    {
        if (!Enum.IsDefined(state)) throw new ArgumentException("A defined state is required.", nameof(state));
        if (state != PerformanceState.Throttled && targetPresentationFps is not null)
            throw new ArgumentException("Only throttled requests may specify presentation FPS.", nameof(targetPresentationFps));
        if (targetPresentationFps is < 1 or > 60)
            throw new ArgumentOutOfRangeException(nameof(targetPresentationFps));
        State = state;
        TargetPresentationFps = targetPresentationFps;
    }

    public PerformanceState State { get; }
    public int? TargetPresentationFps { get; }
}

/// <summary>Tracks renderer lifecycle and performance state without native side effects.</summary>
public sealed class RendererStateMachine
{
    public RendererLifecycle Lifecycle { get; private set; } = RendererLifecycle.Created;

    public PerformanceState PerformanceState { get; private set; } = PerformanceState.Running;

    public void TransitionTo(RendererLifecycle next)
    {
        ThrowIfDisposed();

        if (!Enum.IsDefined(next))
        {
            throw new ArgumentException("A defined lifecycle state is required.", nameof(next));
        }

        if (next == RendererLifecycle.Faulted)
        {
            Lifecycle = RendererLifecycle.Faulted;
            return;
        }

        if (next == RendererLifecycle.Stopped)
        {
            Stop();
            return;
        }

        if (next == RendererLifecycle.Disposed)
        {
            Dispose();
            return;
        }

        if (!IsNextNormalTransition(Lifecycle, next))
        {
            throw new InvalidOperationException($"Cannot transition from {Lifecycle} to {next}.");
        }

        Lifecycle = next;
    }

    public void SetPerformanceState(PerformanceState state)
    {
        ThrowIfDisposed();

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentException("A defined performance state is required.", nameof(state));
        }

        PerformanceState = state;
    }

    public void Stop()
    {
        if (Lifecycle is RendererLifecycle.Disposed or RendererLifecycle.Stopped)
        {
            return;
        }

        Lifecycle = RendererLifecycle.Stopped;
    }

    public void Dispose()
    {
        if (Lifecycle == RendererLifecycle.Disposed)
        {
            return;
        }

        Lifecycle = RendererLifecycle.Disposed;
    }

    private static bool IsNextNormalTransition(RendererLifecycle current, RendererLifecycle next) =>
        (current, next) is
        (RendererLifecycle.Created, RendererLifecycle.Initializing) or
        (RendererLifecycle.Initializing, RendererLifecycle.Loading) or
        (RendererLifecycle.Loading, RendererLifecycle.Ready) or
        (RendererLifecycle.Ready, RendererLifecycle.Active);

    private void ThrowIfDisposed()
    {
        if (Lifecycle == RendererLifecycle.Disposed)
        {
            throw new InvalidOperationException("The renderer is disposed.");
        }
    }
}
