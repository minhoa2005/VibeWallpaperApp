using System.Collections.Concurrent;
using VibeWallpaper.Engine.Core.Activity;

namespace VibeWallpaper.Engine.Activity;

public sealed class ActivityMonitor : IActivityMonitor, IActivityEvidenceSink
{
    public static TimeSpan FallbackInterval { get; } = TimeSpan.FromSeconds(1);

    private readonly object _gate = new();
    private readonly object _builderGate = new();
    private readonly IActivitySnapshotBuilder _builder;
    private readonly IActivityEvidenceConsumer? _evidenceConsumer;
    private readonly ConcurrentQueue<ActivityEvidence> _evidence = new();
    private readonly SelectiveDebouncer _debouncer;
    private readonly ITimer _fallbackTimer;
    private bool _started;
    private bool _disposed;
    private bool _evaluationScheduled;
    private bool _evaluationRequested;
    private int _evaluationsInFlight;
    private ActivitySnapshot? _current;

    public ActivityMonitor(
        IActivitySnapshotBuilder builder,
        TimeProvider? timeProvider = null,
        IActivityEvidenceConsumer? evidenceConsumer = null)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var clock = timeProvider ?? TimeProvider.System;
        _builder = builder;
        _evidenceConsumer = evidenceConsumer;
        _debouncer = new SelectiveDebouncer(clock, ScheduleEvaluation);
        _fallbackTimer = clock.CreateTimer(OnFallbackTick, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public event ActivitySnapshotPublishedHandler? SnapshotPublished;

    public ActivitySnapshot? Current
    {
        get { lock (_gate) return _current; }
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_started) return;
            _started = true;
            _fallbackTimer.Change(FallbackInterval, FallbackInterval);
        }

        EvaluateSynchronously();
    }

    public void Enqueue(ActivityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        lock (_gate)
        {
            if (_disposed || !_started) return;
            _evidence.Enqueue(evidence with { });
            _debouncer.Enqueue(evidence);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_started) return;
            _started = false;
            _fallbackTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _debouncer.Cancel();
        WaitForEvaluationsAndClearEvidence();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _started = false;
            _fallbackTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        _debouncer.Cancel();
        WaitForEvaluationsAndClearEvidence();
        _fallbackTimer.Dispose();
        _debouncer.Dispose();
    }

    private void OnFallbackTick(object? state) => ScheduleEvaluation();

    private void ScheduleEvaluation()
    {
        lock (_gate)
        {
            if (!_started || _disposed) return;
            if (_evaluationScheduled)
            {
                _evaluationRequested = true;
                return;
            }

            _evaluationScheduled = true;
            _evaluationsInFlight++;
        }

        ThreadPool.UnsafeQueueUserWorkItem(
            static monitor => monitor.RunScheduledEvaluations(),
            this,
            preferLocal: false);
    }

    private void RunScheduledEvaluations()
    {
        try
        {
            while (true)
            {
                EvaluateOnce();
                lock (_gate)
                {
                    if (_started && !_disposed && _evaluationRequested)
                    {
                        _evaluationRequested = false;
                        continue;
                    }

                    _evaluationScheduled = false;
                    return;
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                _evaluationScheduled = false;
                _evaluationRequested = false;
                _evaluationsInFlight--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void EvaluateSynchronously()
    {
        lock (_gate) _evaluationsInFlight++;
        try { EvaluateOnce(); }
        finally
        {
            lock (_gate)
            {
                _evaluationsInFlight--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    private void EvaluateOnce()
    {
        ActivitySnapshot snapshot;
        lock (_builderGate)
        {
            lock (_gate)
            {
                if (!_started || _disposed) return;
            }

            while (_evidence.TryDequeue(out var evidence)) _evidenceConsumer?.Apply(evidence);
            snapshot = _builder.Build();
        }

        ActivitySnapshotPublishedHandler? published;
        lock (_gate)
        {
            if (!_started || _disposed) return;
            _current = snapshot;
            published = SnapshotPublished;
        }

        published?.Invoke(this, snapshot);
    }

    private void WaitForEvaluationsAndClearEvidence()
    {
        lock (_gate)
        {
            _evaluationRequested = false;
            while (_evaluationsInFlight != 0) Monitor.Wait(_gate);
            while (_evidence.TryDequeue(out _)) { }
        }
    }
}
