using VibeWallpaper.App.Coordination;

namespace VibeWallpaper.Tests.App;

public sealed class ApplicationCoordinatorTests
{
    private static readonly ApplicationStageKind[] StartupOrder =
    [
        ApplicationStageKind.PerMonitorV2,
        ApplicationStageKind.SingleInstance,
        ApplicationStageKind.LoggingConfigurationState,
        ApplicationStageKind.EngineDispatcher,
        ApplicationStageKind.TopologyAndDesktopHosts,
        ApplicationStageKind.RestoreAssignments,
        ApplicationStageKind.ActivityObservers,
        ApplicationStageKind.TrayAndUi,
    ];

    [Fact]
    public async Task StartAsync_StartsEveryStageInRequiredOrder()
    {
        var events = new List<string>();
        var stages = StartupOrder.Reverse().Select(kind => new RecordingStage(kind, events)).ToArray();
        await using var coordinator = new ApplicationCoordinator(stages, TimeSpan.FromSeconds(1));

        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(StartupOrder.Select(kind => $"start:{kind}"), events);
    }

    [Theory]
    [InlineData(ApplicationStageKind.SingleInstance)]
    [InlineData(ApplicationStageKind.LoggingConfigurationState)]
    [InlineData(ApplicationStageKind.EngineDispatcher)]
    [InlineData(ApplicationStageKind.TopologyAndDesktopHosts)]
    [InlineData(ApplicationStageKind.RestoreAssignments)]
    [InlineData(ApplicationStageKind.ActivityObservers)]
    [InlineData(ApplicationStageKind.TrayAndUi)]
    public async Task StartAsync_WhenStageFails_RollsBackCompletedStagesInReverseOrder(
        ApplicationStageKind failingKind)
    {
        var events = new List<string>();
        var stages = StartupOrder.Select(kind => new RecordingStage(kind, events, kind == failingKind)).ToArray();
        await using var coordinator = new ApplicationCoordinator(stages, TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(TestContext.Current.CancellationToken));

        var attempted = StartupOrder.TakeWhile(kind => kind != failingKind).Append(failingKind).ToArray();
        Assert.Equal(
            attempted.Select(kind => $"start:{kind}")
                .Concat(attempted.Reverse().Select(kind => $"stop:{kind}")),
            events);
    }

    [Fact]
    public async Task StopAsync_IsIdempotentAndUsesReverseStartupOrder()
    {
        var events = new List<string>();
        var stages = StartupOrder.Select(kind => new RecordingStage(kind, events)).ToArray();
        await using var coordinator = new ApplicationCoordinator(stages, TimeSpan.FromSeconds(1));
        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        await coordinator.StopAsync();
        await coordinator.StopAsync();

        Assert.Equal(
            StartupOrder.Select(kind => $"start:{kind}")
                .Concat(StartupOrder.Reverse().Select(kind => $"stop:{kind}")),
            events);
    }

    [Fact]
    public async Task StopAsync_WhenStageDoesNotComplete_ReturnsWithinDeadline()
    {
        var stages = StartupOrder.Select(kind =>
            new RecordingStage(kind, [], blockStop: kind == ApplicationStageKind.EngineDispatcher)).ToArray();
        await using var coordinator = new ApplicationCoordinator(stages, TimeSpan.FromMilliseconds(100));
        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        var started = DateTime.UtcNow;
        await coordinator.StopAsync();

        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(1));
        Assert.True(coordinator.ShutdownTimedOut);
    }

    [Fact]
    public async Task StopAsync_WhenDisposeBlocksBeforeReturningValueTask_StillReturnsTaskImmediatelyAndMeetsDeadline()
    {
        using var release = new ManualResetEventSlim();
        var stages = StartupOrder.Select(kind => kind == ApplicationStageKind.EngineDispatcher
            ? (IApplicationStage)new SynchronouslyBlockingStage(kind, release)
            : new RecordingStage(kind, [])).ToArray();
        await using var coordinator = new ApplicationCoordinator(stages, TimeSpan.FromMilliseconds(100));
        await coordinator.StartAsync(TestContext.Current.CancellationToken);
        Task? shutdown = null;
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caller = new Thread(() =>
        {
            shutdown = coordinator.StopAsync();
            returned.TrySetResult();
        });
        caller.Start();

        var returnedPromptly = await Task.WhenAny(
            returned.Task,
            Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken)) == returned.Task;
        if (returnedPromptly)
        {
            await shutdown!;
        }
        release.Set();
        caller.Join();

        Assert.True(returnedPromptly);
        Assert.True(coordinator.ShutdownTimedOut);
    }

    [Fact]
    public async Task StopAsync_ContinuesPastPerStageTimeout()
    {
        var events = new List<string>();
        var stages = StartupOrder.Select(kind =>
            new RecordingStage(kind, events, blockStop: kind == ApplicationStageKind.EngineDispatcher)).ToArray();
        await using var coordinator = new ApplicationCoordinator(
            stages,
            TimeSpan.FromMilliseconds(150),
            TimeSpan.FromMilliseconds(20));
        await coordinator.StartAsync(TestContext.Current.CancellationToken);

        await coordinator.StopAsync();

        Assert.Contains("stop:TopologyAndDesktopHosts", events);
        Assert.True(coordinator.ShutdownTimedOut);
    }

    private sealed class RecordingStage(
        ApplicationStageKind kind,
        List<string> events,
        bool failStart = false,
        bool blockStop = false) : IApplicationStage
    {
        public ApplicationStageKind Kind => kind;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            events.Add($"start:{Kind}");
            return failStart
                ? Task.FromException(new InvalidOperationException("injected startup failure"))
                : Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            events.Add($"stop:{Kind}");
            return blockStop
                ? new ValueTask(Task.Delay(Timeout.InfiniteTimeSpan))
                : ValueTask.CompletedTask;
        }
    }

    private sealed class SynchronouslyBlockingStage(
        ApplicationStageKind kind,
        ManualResetEventSlim release) : IApplicationStage
    {
        public ApplicationStageKind Kind => kind;
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync()
        {
            release.Wait();
            return ValueTask.CompletedTask;
        }
    }
}
