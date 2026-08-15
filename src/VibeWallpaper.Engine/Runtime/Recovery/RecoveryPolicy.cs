namespace VibeWallpaper.Engine.Runtime.Recovery;

public sealed record RecoveryPolicy
{
    public static RecoveryPolicy Default { get; } = new();

    public IReadOnlyList<TimeSpan> RendererRetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    public TimeSpan RendererAttemptTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public IReadOnlyList<TimeSpan> ExplorerRetryDelays { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)];

    public TimeSpan ShutdownStepTimeout { get; init; } = TimeSpan.FromSeconds(3);

    public TimeSpan ShutdownTotalTimeout { get; init; } = TimeSpan.FromSeconds(10);

    public RecoveryPolicy()
    {
        ValidateDelays(RendererRetryDelays, nameof(RendererRetryDelays));
        ValidateDelays(ExplorerRetryDelays, nameof(ExplorerRetryDelays));
        if (RendererAttemptTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(RendererAttemptTimeout));
        if (ShutdownStepTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ShutdownStepTimeout));
        if (ShutdownTotalTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(ShutdownTotalTimeout));
    }

    private static void ValidateDelays(IReadOnlyList<TimeSpan> delays, string name)
    {
        ArgumentNullException.ThrowIfNull(delays);
        if (delays.Count == 0 || delays.Any(static delay => delay < TimeSpan.Zero))
            throw new ArgumentException("At least one nonnegative delay is required.", name);
    }
}

public interface IRecoveryDelayScheduler
{
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

public sealed class TimeProviderRecoveryDelayScheduler(TimeProvider? timeProvider = null) : IRecoveryDelayScheduler
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        Task.Delay(delay, _timeProvider, cancellationToken);
}

public sealed class RendererRecoveryResult(
    RendererRecoveryStatus status,
    int attempts,
    Exception? lastFailure)
{
    public RendererRecoveryStatus Status { get; } = status;
    public int Attempts { get; } = attempts;
    public Exception? LastFailure { get; } = lastFailure;
}

public enum RendererRecoveryStatus
{
    Recovered,
    Exhausted,
}

public interface IRendererRecoveryTarget
{
    Task RecoverAsync(CancellationToken cancellationToken);
    Task ActivateFallbackAsync(CancellationToken cancellationToken);
}

public enum DesktopRecoveryStatus
{
    Reattached,
    Unavailable,
}

public sealed class DesktopRecoveryResult(DesktopRecoveryStatus status, int attempts, Exception? lastFailure)
{
    public DesktopRecoveryStatus Status { get; } = status;
    public int Attempts { get; } = attempts;
    public Exception? LastFailure { get; } = lastFailure;
}

public interface IDesktopRecoveryOperations
{
    Task HideAndSuspendHostsAsync(CancellationToken cancellationToken);
    void InvalidateShellHandles();
    Task RediscoverAndReattachAsync(CancellationToken cancellationToken);
    Task ReapplyLatestActivitySnapshotAsync(CancellationToken cancellationToken);
}

public enum TopologyReconciliationStatus
{
    Applied,
    Superseded,
}

public sealed class TopologyReconciliationResult(TopologyReconciliationStatus status)
{
    public TopologyReconciliationStatus Status { get; } = status;
}

public interface ITopologyRecoveryOperations
{
    Task PrepareAsync(string topologyVersion, CancellationToken cancellationToken);
    Task CommitAsync(string topologyVersion, CancellationToken cancellationToken);
    Task MarkOutputDisconnectedAsync(string outputKey, CancellationToken cancellationToken);
}

public interface IShutdownStep
{
    string Name { get; }
    ValueTask ExecuteAsync(CancellationToken cancellationToken);
}

public sealed class DelegateShutdownStep(
    string name,
    Func<CancellationToken, ValueTask> execute) : IShutdownStep
{
    private readonly Func<CancellationToken, ValueTask> _execute = execute ?? throw new ArgumentNullException(nameof(execute));

    public string Name { get; } = string.IsNullOrWhiteSpace(name)
        ? throw new ArgumentException("A shutdown step name is required.", nameof(name))
        : name.Trim();

    public ValueTask ExecuteAsync(CancellationToken cancellationToken) => _execute(cancellationToken);
}

public interface IProcessTerminator
{
    void TerminateCurrentProcess(int exitCode);
}

public sealed class CurrentProcessTerminator : IProcessTerminator
{
    public void TerminateCurrentProcess(int exitCode) => Environment.Exit(exitCode);
}

public sealed class ShutdownResult(bool timedOut, IReadOnlyList<string> timedOutSteps)
{
    public bool TimedOut { get; } = timedOut;
    public IReadOnlyList<string> TimedOutSteps { get; } = timedOutSteps;
}
