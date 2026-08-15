using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Runtime.Recovery;

public sealed class RendererRecoveryCoordinator
{
    private readonly RecoveryPolicy _policy;
    private readonly IRecoveryDelayScheduler _delayScheduler;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _exhaustedOutputs = new(StringComparer.Ordinal);
    private readonly object _stateGate = new();

    public RendererRecoveryCoordinator(
        RecoveryPolicy? policy = null,
        IRecoveryDelayScheduler? delayScheduler = null)
    {
        _policy = policy ?? RecoveryPolicy.Default;
        _delayScheduler = delayScheduler ?? new TimeProviderRecoveryDelayScheduler();
    }

    public async Task<RendererRecoveryResult> RecoverAsync(
        MonitorIdentity output,
        IRendererRecoveryTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(target);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateGate)
            {
                if (_exhaustedOutputs.Contains(output.Key))
                    return new(RendererRecoveryStatus.Exhausted, 0, null);
            }

            Exception? lastFailure = null;
            var attempts = 0;
            foreach (var delay in _policy.RendererRetryDelays)
            {
                await _delayScheduler.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
                attempts++;
                using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                try
                {
                    var recovery = target.RecoverAsync(attemptCancellation.Token);
                    await recovery.WaitAsync(_policy.RendererAttemptTimeout, cancellationToken).ConfigureAwait(false);
                    lock (_stateGate) _exhaustedOutputs.Remove(output.Key);
                    return new(RendererRecoveryStatus.Recovered, attempts, null);
                }
                catch (TimeoutException exception)
                {
                    attemptCancellation.Cancel();
                    lastFailure = exception;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    lastFailure = new TimeoutException("Renderer recovery attempt was canceled by its timeout.");
                }
                catch (Exception exception)
                {
                    lastFailure = exception;
                }
            }

            await target.ActivateFallbackAsync(cancellationToken).ConfigureAwait(false);
            lock (_stateGate) _exhaustedOutputs.Add(output.Key);
            return new(RendererRecoveryStatus.Exhausted, attempts, lastFailure);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Reset(MonitorIdentity output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_stateGate) _exhaustedOutputs.Remove(output.Key);
    }
}
