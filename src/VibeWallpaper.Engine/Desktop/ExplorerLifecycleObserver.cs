using VibeWallpaper.Engine.Runtime.Recovery;

namespace VibeWallpaper.Engine.Desktop;

public interface IExplorerMessageSource
{
    uint TaskbarCreatedMessage { get; }
    event Action<uint>? MessageReceived;
}

public interface IExplorerRecoveryTrigger
{
    Task<DesktopRecoveryResult> HandleExplorerInvalidationAsync(CancellationToken cancellationToken);
}

public sealed class ExplorerLifecycleObserver(
    IExplorerMessageSource messageSource,
    IExplorerRecoveryTrigger recovery) : IAsyncDisposable
{
    private readonly IExplorerMessageSource _messageSource = messageSource ?? throw new ArgumentNullException(nameof(messageSource));
    private readonly IExplorerRecoveryTrigger _recovery = recovery ?? throw new ArgumentNullException(nameof(recovery));
    private int _started;
    private int _disposed;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (Interlocked.Exchange(ref _started, 1) == 0)
            _messageSource.MessageReceived += OnMessage;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0 && Interlocked.Exchange(ref _started, 0) != 0)
            _messageSource.MessageReceived -= OnMessage;
        return ValueTask.CompletedTask;
    }

    private void OnMessage(uint message)
    {
        if (message != _messageSource.TaskbarCreatedMessage || Volatile.Read(ref _disposed) != 0)
            return;

        _ = HandleRecoveryAsync();
    }

    private async Task HandleRecoveryAsync()
    {
        try
        {
            await _recovery.HandleExplorerInvalidationAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Recovery is retried by the coordinator; an observer callback must not
            // surface an exception to the native message source.
        }
    }
}
