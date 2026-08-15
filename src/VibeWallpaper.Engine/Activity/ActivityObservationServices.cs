using VibeWallpaper.Engine.Core.Activity;

namespace VibeWallpaper.Engine.Activity;

public sealed class ActivityObservationServices : IAsyncDisposable
{
    private readonly object _publicationGate = new();
    private readonly IActivityMonitor _monitor;
    private readonly IReadOnlyList<IActivityObserver> _observers;
    private readonly Func<ActivitySnapshot, CancellationToken, Task> _publish;
    private readonly CancellationTokenSource _shutdown = new();
    private Task _publicationTail = Task.CompletedTask;
    private bool _started;
    private bool _disposed;

    public ActivityObservationServices(
        IActivityMonitor monitor,
        IReadOnlyList<IActivityObserver> observers,
        Func<ActivitySnapshot, CancellationToken, Task> publish)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(observers);
        ArgumentNullException.ThrowIfNull(publish);
        if (observers.Any(static observer => observer is null))
            throw new ArgumentException("Observers cannot contain null.", nameof(observers));
        _monitor = monitor;
        _observers = observers.ToArray();
        _publish = publish;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_started) throw new InvalidOperationException("Activity observation has already started.");
        _monitor.SnapshotPublished += OnSnapshotPublished;
        var started = new List<IActivityObserver>();
        try
        {
            foreach (var observer in _observers)
            {
                observer.Start();
                started.Add(observer);
            }

            _monitor.Start();
            _started = true;
        }
        catch
        {
            _monitor.Stop();
            foreach (var observer in started.AsEnumerable().Reverse()) observer.Dispose();
            _monitor.SnapshotPublished -= OnSnapshotPublished;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.Stop();
        _monitor.SnapshotPublished -= OnSnapshotPublished;
        foreach (var observer in _observers.Reverse()) observer.Dispose();
        await _shutdown.CancelAsync();
        Task tail;
        lock (_publicationGate) tail = _publicationTail;
        try { await tail; }
        catch (OperationCanceledException) { }
        _monitor.Dispose();
        _shutdown.Dispose();
    }

    private void OnSnapshotPublished(object? sender, ActivitySnapshot snapshot)
    {
        lock (_publicationGate)
        {
            if (_disposed) return;
            _publicationTail = _publicationTail.ContinueWith(
                _ => _publish(snapshot, _shutdown.Token),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default).Unwrap();
        }
    }
}
