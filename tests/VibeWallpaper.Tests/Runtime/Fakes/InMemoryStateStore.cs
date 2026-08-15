using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Tests.Runtime.Fakes;

internal sealed class InMemoryStateStore(PersistedState? initial = null) : IStateStore
{
    private readonly object _gate = new();

    public PersistedState State { get; private set; } = initial ?? PersistedState.Default;
    public int SaveCount { get; private set; }
    public Exception? NextSaveFailure { get; set; }
    public TaskCompletionSource? SaveStarted { get; set; }
    public TaskCompletionSource? SaveRelease { get; set; }
    public bool SaveObservesCancellation { get; set; } = true;

    public void Replace(PersistedState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            State = state;
        }
    }

    public Task<PersistenceLoadResult<PersistedState>> LoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new PersistenceLoadResult<PersistedState>(State, PersistenceLoadSource.Primary, null));

    public async Task SaveAsync(PersistedState state, CancellationToken cancellationToken)
    {
        SaveStarted?.TrySetResult();
        if (SaveRelease is not null)
        {
            if (SaveObservesCancellation)
            {
                await SaveRelease.Task.WaitAsync(cancellationToken);
            }
            else
            {
                await SaveRelease.Task;
            }
        }

        lock (_gate)
        {
            if (NextSaveFailure is { } failure)
            {
                NextSaveFailure = null;
                throw failure;
            }

            State = state;
            SaveCount++;
        }
    }
}
