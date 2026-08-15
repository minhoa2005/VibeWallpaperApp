using System.Collections.Concurrent;

namespace VibeWallpaper.Engine.Runtime;

internal sealed class EngineObservationTracker
{
    private readonly ConcurrentDictionary<Task, byte> _pending = new();
    private long _completedCount;

    internal int PendingCount => _pending.Count;
    internal long CompletedCount => Interlocked.Read(ref _completedCount);

    internal void Track(Task observation)
    {
        if (observation.IsCompleted)
        {
            Interlocked.Increment(ref _completedCount);
            return;
        }

        _pending.TryAdd(observation, 0);
        _ = observation.ContinueWith(
            static (completed, state) => ((EngineObservationTracker)state!).Complete(completed),
            this,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    internal void ReleasePendingRegistry() => _pending.Clear();

    private void Complete(Task observation)
    {
        _pending.TryRemove(observation, out _);
        Interlocked.Increment(ref _completedCount);
    }
}
