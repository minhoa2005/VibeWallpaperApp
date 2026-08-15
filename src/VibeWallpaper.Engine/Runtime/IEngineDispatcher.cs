namespace VibeWallpaper.Engine.Runtime;

public interface IEngineDispatcher : IAsyncDisposable
{
    bool HasThreadAccess { get; }

    /// <remarks>
    /// Engine delegates must retain the dispatcher synchronization context across awaits when
    /// later code touches live engine or native state; do not use <c>ConfigureAwait(false)</c>
    /// for those continuations.
    /// </remarks>
    Task InvokeAsync(
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default);

    /// <remarks>
    /// Engine delegates must retain the dispatcher synchronization context across awaits when
    /// later code touches live engine or native state; do not use <c>ConfigureAwait(false)</c>
    /// for those continuations.
    /// </remarks>
    Task<T> InvokeAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken = default);
}
