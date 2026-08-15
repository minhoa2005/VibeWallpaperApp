using VibeWallpaper.Engine.Runtime.Recovery;

namespace VibeWallpaper.Tests.Runtime;

public sealed class ShutdownCoordinatorTests
{
    [Fact]
    public async Task Shutdown_RejectsNewWork_UsesOrderAndContinuesPastSlowStep()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = new List<string>();
        var terminator = new RecordingTerminator();
        var policy = RecoveryPolicy.Default with
        {
            ShutdownStepTimeout = TimeSpan.FromMilliseconds(20),
            ShutdownTotalTimeout = TimeSpan.FromMilliseconds(70),
        };
        var coordinator = new ShutdownCoordinator(
            [
                new DelegateShutdownStep("interaction", _ => { calls.Add("interaction"); return ValueTask.CompletedTask; }),
                new DelegateShutdownStep("activity", _ => { calls.Add("activity"); return ValueTask.CompletedTask; }),
                new DelegateShutdownStep("renderers", async _ =>
                {
                    calls.Add("renderers");
                    await release.Task;
                }),
                new DelegateShutdownStep("hosts", _ => { calls.Add("hosts"); return ValueTask.CompletedTask; }),
            ],
            terminator,
            policy);

        Assert.True(coordinator.TryBeginWork());
        var result = await coordinator.ShutdownAsync();

        Assert.False(coordinator.TryBeginWork());
        Assert.True(result.TimedOut);
        Assert.Equal(["interaction", "activity", "renderers", "hosts"], calls);
        Assert.Equal(1, terminator.Calls);
        release.TrySetResult();
    }

    [Fact]
    public async Task Shutdown_IsIdempotentAndDoesNotTerminateWhenAllStepsFinish()
    {
        var terminator = new RecordingTerminator();
        var coordinator = new ShutdownCoordinator(
            [new DelegateShutdownStep("only", _ => ValueTask.CompletedTask)],
            terminator,
            RecoveryPolicy.Default with
            {
                ShutdownStepTimeout = TimeSpan.FromSeconds(1),
                ShutdownTotalTimeout = TimeSpan.FromSeconds(2),
            });

        var first = await coordinator.ShutdownAsync();
        var second = await coordinator.ShutdownAsync();

        Assert.False(first.TimedOut);
        Assert.Same(first, second);
        Assert.Equal(0, terminator.Calls);
    }

    private sealed class RecordingTerminator : IProcessTerminator
    {
        public int Calls { get; private set; }
        public void TerminateCurrentProcess(int exitCode) => Calls++;
    }
}
