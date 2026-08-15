using VibeWallpaper.Engine.Runtime.Recovery;
using VibeWallpaper.Engine.Desktop;

namespace VibeWallpaper.Tests.Runtime;

public sealed class ExplorerLifecycleObserverTests
{
    [Fact]
    public async Task Start_HandlesOnlyRegisteredTaskbarCreatedMessageAndStopsAfterDispose()
    {
        var source = new MessageSource(0xC001);
        var recovery = new RecordingRecovery();
        await using var observer = new ExplorerLifecycleObserver(source, recovery);

        observer.Start();
        source.Publish(0xC002);
        Assert.Equal(0, recovery.Calls);
        source.Publish(0xC001);
        await recovery.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(1, recovery.Calls);

        await observer.DisposeAsync();
        source.Publish(0xC001);
        Assert.Equal(1, recovery.Calls);
    }

    private sealed class MessageSource(uint taskbarCreatedMessage) : IExplorerMessageSource
    {
        public uint TaskbarCreatedMessage { get; } = taskbarCreatedMessage;
        public event Action<uint>? MessageReceived;
        public void Publish(uint message) => MessageReceived?.Invoke(message);
    }

    private sealed class RecordingRecovery : IExplorerRecoveryTrigger
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<DesktopRecoveryResult> HandleExplorerInvalidationAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Started.TrySetResult();
            return Task.FromResult(new DesktopRecoveryResult(DesktopRecoveryStatus.Reattached, 1, null));
        }
    }
}
