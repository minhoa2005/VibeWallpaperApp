namespace VibeWallpaper.Engine.Runtime.Recovery;

public sealed class TopologyCoordinator
{
    private readonly ITopologyRecoveryOperations _operations;
    private readonly object _gate = new();
    private CancellationTokenSource? _currentCancellation;
    private long _generation;

    public TopologyCoordinator(ITopologyRecoveryOperations operations)
    {
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
    }

    public Task<TopologyReconciliationResult> ReconcileAsync(
        string topologyVersion,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topologyVersion);
        long generation;
        CancellationToken token;
        lock (_gate)
        {
            _currentCancellation?.Cancel();
            _currentCancellation?.Dispose();
            _currentCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            generation = ++_generation;
            token = _currentCancellation.Token;
        }

        return ReconcileCoreAsync(topologyVersion, generation, token, cancellationToken);
    }

    public async Task InvalidateOutputAsync(string outputKey, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputKey);
        lock (_gate)
        {
            ++_generation;
            _currentCancellation?.Cancel();
        }

        await _operations.MarkOutputDisconnectedAsync(outputKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task<TopologyReconciliationResult> ReconcileCoreAsync(
        string topologyVersion,
        long generation,
        CancellationToken cancellationToken,
        CancellationToken callerCancellationToken)
    {
        try
        {
            await _operations.PrepareAsync(topologyVersion, cancellationToken).ConfigureAwait(false);
            if (!IsCurrent(generation, cancellationToken))
                return new(TopologyReconciliationStatus.Superseded);

            await _operations.CommitAsync(topologyVersion, cancellationToken).ConfigureAwait(false);
            return new(TopologyReconciliationStatus.Applied);
        }
        catch (OperationCanceledException) when (!callerCancellationToken.IsCancellationRequested)
        {
            return new(TopologyReconciliationStatus.Superseded);
        }
        finally
        {
            lock (_gate)
            {
                if (generation == _generation)
                {
                    _currentCancellation?.Dispose();
                    _currentCancellation = null;
                }
            }
        }
    }

    private bool IsCurrent(long generation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            return generation == _generation && !cancellationToken.IsCancellationRequested;
        }
    }

}
