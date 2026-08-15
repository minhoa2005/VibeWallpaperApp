using VibeWallpaper.Engine.Runtime.Recovery;

namespace VibeWallpaper.Tests.Runtime;

public sealed class DesktopRecoveryCoordinatorTests
{
    [Fact]
    public async Task HandleExplorerInvalidation_HidesInvalidatesThenReattachesAndReapplies()
    {
        var operations = new RecordingDesktopRecoveryOperations();
        var delays = new RecordingDelayScheduler();
        var policy = RecoveryPolicy.Default with { ExplorerRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] };
        var coordinator = new DesktopRecoveryCoordinator(policy, operations, delays);

        var result = await coordinator.HandleExplorerInvalidationAsync(CancellationToken.None);

        Assert.Equal(DesktopRecoveryStatus.Reattached, result.Status);
        Assert.Equal(["hide", "invalidate", "rediscover", "reattach", "activity"], operations.Calls);
        Assert.Equal(1, operations.HideAndSuspendCalls);
    }

    [Fact]
    public async Task HandleExplorerInvalidation_StopsAfterBoundedRediscoveryAndNeverRestartsExplorer()
    {
        var operations = new RecordingDesktopRecoveryOperations { RediscoveryFailures = 4 };
        var delays = new RecordingDelayScheduler();
        var policy = RecoveryPolicy.Default with { ExplorerRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] };
        var coordinator = new DesktopRecoveryCoordinator(policy, operations, delays);

        var result = await coordinator.HandleExplorerInvalidationAsync(CancellationToken.None);

        Assert.Equal(DesktopRecoveryStatus.Unavailable, result.Status);
        Assert.Equal(3, operations.RediscoveryAttempts);
        Assert.DoesNotContain("restart-explorer", operations.Calls);
    }

    private sealed class RecordingDelayScheduler : IRecoveryDelayScheduler
    {
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingDesktopRecoveryOperations : IDesktopRecoveryOperations
    {
        public List<string> Calls { get; } = [];
        public int HideAndSuspendCalls { get; private set; }
        public int RediscoveryAttempts { get; private set; }
        public int RediscoveryFailures { get; init; }

        public Task HideAndSuspendHostsAsync(CancellationToken cancellationToken)
        {
            Calls.Add("hide");
            HideAndSuspendCalls++;
            return Task.CompletedTask;
        }

        public void InvalidateShellHandles() => Calls.Add("invalidate");

        public Task RediscoverAndReattachAsync(CancellationToken cancellationToken)
        {
            Calls.Add("rediscover");
            RediscoveryAttempts++;
            if (RediscoveryAttempts <= RediscoveryFailures)
                throw new InvalidOperationException("Explorer is still unavailable");
            Calls.Add("reattach");
            return Task.CompletedTask;
        }

        public Task ReapplyLatestActivitySnapshotAsync(CancellationToken cancellationToken)
        {
            Calls.Add("activity");
            return Task.CompletedTask;
        }
    }
}
