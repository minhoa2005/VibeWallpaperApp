using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class AsyncCommitGateTests
{
    [Fact]
    public async Task EnterAsync_WhenHolderIsReleased_ResumesWaiterWithoutInlineReentrancy()
    {
        var gate = new AsyncCommitGate();
        var first = await gate.EnterAsync(TestContext.Current.CancellationToken);
        var waiterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiter = WaitAndReenterAsync();

        first.Dispose();
        await waiter.WaitAsync(TestContext.Current.CancellationToken);

        async Task WaitAndReenterAsync()
        {
            using (await gate.EnterAsync(TestContext.Current.CancellationToken))
            {
                waiterEntered.TrySetResult();
            }

            using (await gate.EnterAsync(TestContext.Current.CancellationToken))
            {
                Assert.True(waiterEntered.Task.IsCompleted);
            }
        }
    }

    [Fact]
    public async Task EnterAsync_WhenCanceledWhileQueued_DoesNotConsumeTheNextPermit()
    {
        var gate = new AsyncCommitGate();
        var holder = await gate.EnterAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var canceledWaiter = gate.EnterAsync(cancellation.Token).AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceledWaiter);
        holder.Dispose();

        using var next = await gate.EnterAsync(TestContext.Current.CancellationToken);
    }
}
