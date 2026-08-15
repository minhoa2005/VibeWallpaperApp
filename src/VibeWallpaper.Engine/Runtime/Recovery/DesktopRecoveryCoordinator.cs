namespace VibeWallpaper.Engine.Runtime.Recovery;

public sealed class DesktopRecoveryCoordinator : VibeWallpaper.Engine.Desktop.IExplorerRecoveryTrigger
{
    private readonly RecoveryPolicy _policy;
    private readonly IDesktopRecoveryOperations _operations;
    private readonly IRecoveryDelayScheduler _delayScheduler;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public DesktopRecoveryCoordinator(
        RecoveryPolicy? policy,
        IDesktopRecoveryOperations operations,
        IRecoveryDelayScheduler? delayScheduler = null)
    {
        _policy = policy ?? RecoveryPolicy.Default;
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        _delayScheduler = delayScheduler ?? new TimeProviderRecoveryDelayScheduler();
    }

    public async Task<DesktopRecoveryResult> HandleExplorerInvalidationAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _operations.HideAndSuspendHostsAsync(cancellationToken).ConfigureAwait(false);
            _operations.InvalidateShellHandles();

            Exception? lastFailure = null;
            var attempts = 0;
            foreach (var delay in _policy.ExplorerRetryDelays)
            {
                await _delayScheduler.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                attempts++;
                try
                {
                    await _operations.RediscoverAndReattachAsync(cancellationToken).ConfigureAwait(false);
                    await _operations.ReapplyLatestActivitySnapshotAsync(cancellationToken).ConfigureAwait(false);
                    return new(DesktopRecoveryStatus.Reattached, attempts, null);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    lastFailure = exception;
                }
            }

            return new(DesktopRecoveryStatus.Unavailable, attempts, lastFailure);
        }
        finally
        {
            _gate.Release();
        }
    }
}
