using VibeWallpaper.Engine.Runtime.Recovery;

namespace VibeWallpaper.Tests.Runtime;

public sealed class TopologyCoordinatorRaceTests
{
    [Fact]
    public async Task InvalidationDuringPrepare_CancelsGenerationAndRejectsLateCommit()
    {
        var operations = new RecordingTopologyOperations();
        var coordinator = new TopologyCoordinator(operations);
        var reconcile = coordinator.ReconcileAsync("topology-1", CancellationToken.None);
        await operations.PrepareStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        await coordinator.InvalidateOutputAsync("MONITOR-A", CancellationToken.None);
        operations.ReleasePrepare.TrySetResult();
        var result = await reconcile;

        Assert.Equal(TopologyReconciliationStatus.Superseded, result.Status);
        Assert.Equal(0, operations.CommitCalls);
        Assert.Equal(["disconnect:MONITOR-A"], operations.Calls);
    }

    [Fact]
    public async Task NewerReconcileSupersedesOlderGenerationWithoutDoubleCommit()
    {
        var operations = new RecordingTopologyOperations();
        var coordinator = new TopologyCoordinator(operations);
        var first = coordinator.ReconcileAsync("topology-1", CancellationToken.None);
        await operations.PrepareStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = coordinator.ReconcileAsync("topology-2", CancellationToken.None);
        operations.ReleasePrepare.TrySetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Contains(results, item => item.Status == TopologyReconciliationStatus.Superseded);
        Assert.Contains(results, item => item.Status == TopologyReconciliationStatus.Applied);
        Assert.Equal(1, operations.CommitCalls);
        Assert.Equal("topology-2", operations.CommittedTopology);
    }

    [Fact]
    public async Task CallerCancellation_IsPropagatedRatherThanReportedAsSuperseded()
    {
        var operations = new RecordingTopologyOperations();
        var coordinator = new TopologyCoordinator(operations);
        using var cancellation = new CancellationTokenSource();
        var reconcile = coordinator.ReconcileAsync("topology-1", cancellation.Token);
        await operations.PrepareStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconcile);
    }

    private sealed class RecordingTopologyOperations : ITopologyRecoveryOperations
    {
        public TaskCompletionSource PrepareStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleasePrepare { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Calls { get; } = [];
        public int CommitCalls { get; private set; }
        public string? CommittedTopology { get; private set; }

        public async Task PrepareAsync(string topologyVersion, CancellationToken cancellationToken)
        {
            PrepareStarted.TrySetResult();
            await ReleasePrepare.Task.WaitAsync(cancellationToken);
        }

        public Task CommitAsync(string topologyVersion, CancellationToken cancellationToken)
        {
            CommitCalls++;
            CommittedTopology = topologyVersion;
            return Task.CompletedTask;
        }

        public Task MarkOutputDisconnectedAsync(string outputKey, CancellationToken cancellationToken)
        {
            Calls.Add($"disconnect:{outputKey}");
            return Task.CompletedTask;
        }
    }
}
