namespace VibeWallpaper.Engine.Activity;

public sealed class SelectiveDebouncer : IDisposable
{
    public static TimeSpan DefaultDelay { get; } = TimeSpan.FromMilliseconds(400);

    private readonly object _gate = new();
    private readonly Action _callback;
    private readonly TimeSpan _delay;
    private readonly ITimer _timer;
    private bool _armed;
    private int _callbacksInFlight;
    private bool _disposed;

    public SelectiveDebouncer(TimeProvider timeProvider, Action callback, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(callback);
        _delay = delay ?? DefaultDelay;
        if (_delay <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(delay));
        _callback = callback;
        _timer = timeProvider.CreateTimer(OnTimer, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void Enqueue(ActivityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _armed = true;
            if (evidence.RequiresImmediateEvaluation)
            {
                _timer.Change(TimeSpan.Zero, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _timer.Change(_delay, Timeout.InfiniteTimeSpan);
            }
        }

    }

    public void Cancel()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _armed = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            while (_callbacksInFlight != 0) Monitor.Wait(_gate);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _armed = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            while (_callbacksInFlight != 0) Monitor.Wait(_gate);
            _timer.Dispose();
        }
    }

    private void OnTimer(object? state)
    {
        lock (_gate)
        {
            if (_disposed || !_armed) return;
            _armed = false;
            _callbacksInFlight++;
        }

        try { _callback(); }
        finally
        {
            lock (_gate)
            {
                _callbacksInFlight--;
                Monitor.PulseAll(_gate);
            }
        }
    }
}
