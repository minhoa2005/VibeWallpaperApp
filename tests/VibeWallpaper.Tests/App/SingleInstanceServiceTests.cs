using VibeWallpaper.App.Services;

namespace VibeWallpaper.Tests.App;

public sealed class SingleInstanceServiceTests
{
    [Fact]
    public async Task StartAsync_SecondInstanceSendsActivateAndReturnsSecondary()
    {
        var instanceNamespace = $"VibeWallpaper.Tests.{Guid.NewGuid():N}";
        var dispatcher = new RecordingActivationDispatcher();
        var activated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var first = new SingleInstanceService(instanceNamespace, dispatcher);

        var firstResult = await first.StartAsync(
            () =>
            {
                activated.TrySetResult();
                return Task.CompletedTask;
            },
            TestContext.Current.CancellationToken);
        await using var second = new SingleInstanceService(instanceNamespace, new InlineActivationDispatcher());
        var secondResult = await second.StartAsync(
            () => Task.FromException(new InvalidOperationException("secondary callback must not run")),
            TestContext.Current.CancellationToken);

        await activated.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(SingleInstanceStartResult.Primary, firstResult);
        Assert.Equal(SingleInstanceStartResult.SecondaryActivationSent, secondResult);
        Assert.Equal(1, dispatcher.DispatchCount);
    }

    [Fact]
    public async Task DisposeAsync_ReleasesUniqueNamespaceForNextPrimary()
    {
        var instanceNamespace = $"VibeWallpaper.Tests.{Guid.NewGuid():N}";
        await using (var first = new SingleInstanceService(instanceNamespace, new InlineActivationDispatcher()))
        {
            Assert.Equal(
                SingleInstanceStartResult.Primary,
                await first.StartAsync(() => Task.CompletedTask, TestContext.Current.CancellationToken));
        }

        await using var replacement = new SingleInstanceService(instanceNamespace, new InlineActivationDispatcher());
        Assert.Equal(
            SingleInstanceStartResult.Primary,
            await replacement.StartAsync(() => Task.CompletedTask, TestContext.Current.CancellationToken));
    }

    private sealed class RecordingActivationDispatcher : IActivationDispatcher
    {
        public int DispatchCount { get; private set; }

        public Task DispatchAsync(Func<Task> callback)
        {
            DispatchCount++;
            return callback();
        }
    }
}
