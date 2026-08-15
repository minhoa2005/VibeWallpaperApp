namespace VibeWallpaper.Engine.Runtime;

/// <summary>
/// A cancellation-aware asynchronous mutex used only for the short commit phase of a transition.
/// </summary>
public sealed class AsyncCommitGate
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);

    public async ValueTask<Lease> EnterAsync(CancellationToken cancellationToken = default)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Lease(_semaphore);
    }

    public sealed class Lease : IDisposable, IAsyncDisposable
    {
        private SemaphoreSlim? _semaphore;

        internal Lease(SemaphoreSlim semaphore) => _semaphore = semaphore;

        public void Dispose() => Interlocked.Exchange(ref _semaphore, null)?.Release();

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
