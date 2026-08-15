using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Runtime.Recovery;

namespace VibeWallpaper.Tests.Runtime;

public sealed class RendererRecoveryCoordinatorTests
{
    [Fact]
    public async Task RecoverAsync_UsesOneTwoFiveSecondBackoffAndStopsAfterThreeAttempts()
    {
        var delays = new RecordingDelayScheduler();
        var target = new RecoveryTarget { FailuresBeforeSuccess = 4 };
        var policy = RecoveryPolicy.Default with
        {
            RendererRetryDelays = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)],
            RendererAttemptTimeout = TimeSpan.FromMilliseconds(50),
        };
        var coordinator = new RendererRecoveryCoordinator(policy, delays);

        var result = await coordinator.RecoverAsync(new MonitorIdentity("A"), target, CancellationToken.None);

        Assert.Equal(RendererRecoveryStatus.Exhausted, result.Status);
        Assert.Equal(3, target.RecoveryAttempts);
        Assert.Equal(1, target.FallbackActivations);
        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)],
            delays.Requested);
    }

    [Fact]
    public async Task RecoverAsync_EnforcesPerAttemptTimeoutAndPreservesOtherOutputs()
    {
        var delays = new RecordingDelayScheduler();
        var target = new RecoveryTarget { IgnoreCancellation = true };
        var policy = RecoveryPolicy.Default with
        {
            RendererRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero],
            RendererAttemptTimeout = TimeSpan.FromMilliseconds(20),
        };
        var coordinator = new RendererRecoveryCoordinator(policy, delays);

        var result = await coordinator.RecoverAsync(new MonitorIdentity("A"), target, CancellationToken.None);

        Assert.Equal(RendererRecoveryStatus.Exhausted, result.Status);
        Assert.Equal(3, target.RecoveryAttempts);
        Assert.Equal(1, target.FallbackActivations);
    }

    [Fact]
    public async Task RecoverAsync_CancelsTimedOutAttempt()
    {
        var target = new RecoveryTarget { IgnoreCancellation = true };
        var policy = RecoveryPolicy.Default with
        {
            RendererRetryDelays = [TimeSpan.Zero],
            RendererAttemptTimeout = TimeSpan.FromMilliseconds(10),
        };
        var coordinator = new RendererRecoveryCoordinator(policy, new RecordingDelayScheduler());

        await coordinator.RecoverAsync(new MonitorIdentity("A"), target, CancellationToken.None);

        await target.CancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Reset_AllowsManualRetryAfterAutomaticExhaustion()
    {
        var delays = new RecordingDelayScheduler();
        var target = new RecoveryTarget { FailuresBeforeSuccess = 3 };
        var policy = RecoveryPolicy.Default with { RendererRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] };
        var coordinator = new RendererRecoveryCoordinator(policy, delays);

        var first = await coordinator.RecoverAsync(new MonitorIdentity("A"), target, CancellationToken.None);
        coordinator.Reset(new MonitorIdentity("A"));
        target.FailuresBeforeSuccess = 0;
        var second = await coordinator.RecoverAsync(new MonitorIdentity("A"), target, CancellationToken.None);

        Assert.Equal(RendererRecoveryStatus.Exhausted, first.Status);
        Assert.Equal(RendererRecoveryStatus.Recovered, second.Status);
        Assert.Equal(4, target.RecoveryAttempts);
        Assert.Equal(1, target.FallbackActivations);
    }

    [Fact]
    public async Task RecoverAsync_AfterExhaustionDoesNotStartAnotherAutomaticRun()
    {
        var target = new RecoveryTarget { FailuresBeforeSuccess = 10 };
        var coordinator = new RendererRecoveryCoordinator(
            RecoveryPolicy.Default with { RendererRetryDelays = [TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero] },
            new RecordingDelayScheduler());
        var output = new MonitorIdentity("A");

        await coordinator.RecoverAsync(output, target, CancellationToken.None);
        var second = await coordinator.RecoverAsync(output, target, CancellationToken.None);

        Assert.Equal(RendererRecoveryStatus.Exhausted, second.Status);
        Assert.Equal(3, target.RecoveryAttempts);
        Assert.Equal(1, target.FallbackActivations);
    }

    private sealed class RecordingDelayScheduler : IRecoveryDelayScheduler
    {
        public List<TimeSpan> Requested { get; } = [];

        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Requested.Add(delay);
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryTarget : IRendererRecoveryTarget
    {
        public int FailuresBeforeSuccess { get; set; }
        public bool IgnoreCancellation { get; set; }
        public int RecoveryAttempts { get; private set; }
        public int FallbackActivations { get; private set; }
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task RecoverAsync(CancellationToken cancellationToken)
        {
            RecoveryAttempts++;
            if (IgnoreCancellation)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException) { CancellationObserved.TrySetResult(); throw; }
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (RecoveryAttempts <= FailuresBeforeSuccess)
                throw new InvalidOperationException("recoverable failure");
        }

        public Task ActivateFallbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FallbackActivations++;
            return Task.CompletedTask;
        }
    }
}
