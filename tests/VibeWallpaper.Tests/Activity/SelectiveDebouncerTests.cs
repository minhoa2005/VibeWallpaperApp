using VibeWallpaper.Engine.Activity;
using VibeWallpaper.Engine.Core.Activity;

namespace VibeWallpaper.Tests.Activity;

public sealed class SelectiveDebouncerTests
{
    [Theory]
    [InlineData(ActivityEvidenceKind.ForegroundChanged)]
    [InlineData(ActivityEvidenceKind.ZOrderChanged)]
    [InlineData(ActivityEvidenceKind.LocationChanged)]
    [InlineData(ActivityEvidenceKind.FullscreenChanged)]
    [InlineData(ActivityEvidenceKind.TopologyReconciled)]
    public void Enqueue_NoisyEvidence_WaitsFourHundredMillisecondsAndCoalesces(ActivityEvidenceKind kind)
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        using var debouncer = new SelectiveDebouncer(time, () => calls++);

        debouncer.Enqueue(new ActivityEvidence(kind));
        time.Advance(TimeSpan.FromMilliseconds(399));
        debouncer.Enqueue(new ActivityEvidence(kind));
        time.Advance(TimeSpan.FromMilliseconds(399));
        Assert.Equal(0, calls);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(ActivityEvidenceKind.SessionLocked)]
    [InlineData(ActivityEvidenceKind.SystemSleeping)]
    [InlineData(ActivityEvidenceKind.DisplayOff)]
    [InlineData(ActivityEvidenceKind.ExplicitPauseChanged)]
    [InlineData(ActivityEvidenceKind.HostInvalidated)]
    [InlineData(ActivityEvidenceKind.MonitorRemoved)]
    [InlineData(ActivityEvidenceKind.Shutdown)]
    public void Enqueue_SafetyEvidence_PublishesImmediately(ActivityEvidenceKind kind)
    {
        var time = new ManualTimeProvider();
        var calls = 0;
        using var debouncer = new SelectiveDebouncer(time, () => calls++);

        debouncer.Enqueue(new ActivityEvidence(kind));
        time.Advance(TimeSpan.Zero);

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Cancel_IgnoresTimerCallbackThatWasAlreadyQueued()
    {
        var time = new QueuedCallbackTimeProvider();
        var calls = 0;
        using var debouncer = new SelectiveDebouncer(time, () => calls++);
        debouncer.Enqueue(new ActivityEvidence(ActivityEvidenceKind.ForegroundChanged));

        debouncer.Cancel();
        time.FireQueuedCallback();

        Assert.Equal(0, calls);
    }

    [Fact]
    public void FallbackTick_RebuildsACompleteSnapshotAndCatchesMissedUnlock()
    {
        var time = new ManualTimeProvider();
        var source = new MutableSnapshotBuilder { Snapshot = Snapshot(locked: true) };
        using var rebuilt = new ManualResetEventSlim();
        source.OnBuild = count => { if (count == 2) rebuilt.Set(); };
        using var monitor = new ActivityMonitor(source, time);
        var published = new List<ActivitySnapshot>();
        monitor.SnapshotPublished += (_, snapshot) => published.Add(snapshot);
        monitor.Start();
        source.Snapshot = Snapshot(locked: false);

        time.Advance(TimeSpan.FromSeconds(1));
        rebuilt.Wait(TestContext.Current.CancellationToken);

        Assert.Equal(2, published.Count);
        Assert.True(published[0].SessionLocked);
        Assert.False(published[1].SessionLocked);
        Assert.Equal(2, source.BuildCount);
    }

    [Fact]
    public void ImmediateEvidence_ReturnsFromCallbackThreadBeforeCaptureOrPublication()
    {
        var time = new ManualTimeProvider();
        var source = new MutableSnapshotBuilder();
        using var rebuilt = new ManualResetEventSlim();
        source.OnBuild = count => { if (count == 2) rebuilt.Set(); };
        using var monitor = new ActivityMonitor(source, time);
        monitor.Start();
        var initialBuildCount = source.BuildCount;
        var callbackThread = 0;
        var callbackReturned = new ManualResetEventSlim();
        var callback = new Thread(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            monitor.Enqueue(new ActivityEvidence(ActivityEvidenceKind.SessionLocked));
            callbackReturned.Set();
        });

        callback.Start();
        callbackReturned.Wait(TestContext.Current.CancellationToken);

        Assert.Equal(initialBuildCount, source.BuildCount);
        time.Advance(TimeSpan.Zero);
        callback.Join();
        rebuilt.Wait(TestContext.Current.CancellationToken);
        Assert.Equal(initialBuildCount + 1, source.BuildCount);
        Assert.NotEqual(callbackThread, source.LastBuildThread);
    }

    [Fact]
    public void ImmediateEvidence_WithInlineTimerStillEvaluatesOffTheCallbackThread()
    {
        var source = new MutableSnapshotBuilder();
        using var rebuilt = new ManualResetEventSlim();
        source.OnBuild = count => { if (count == 2) rebuilt.Set(); };
        using var monitor = new ActivityMonitor(source, new InlineZeroTimeProvider());
        monitor.Start();
        var callbackThread = 0;
        var callback = new Thread(() =>
        {
            callbackThread = Environment.CurrentManagedThreadId;
            monitor.Enqueue(new ActivityEvidence(ActivityEvidenceKind.SessionLocked));
        });

        callback.Start();
        callback.Join();
        rebuilt.Wait(TestContext.Current.CancellationToken);

        Assert.NotEqual(callbackThread, source.LastBuildThread);
    }

    [Fact]
    public void Stop_CancelsPendingDebounceAndIgnoresLateCallbackEvidence()
    {
        var time = new ManualTimeProvider();
        var source = new MutableSnapshotBuilder();
        using var monitor = new ActivityMonitor(source, time);
        monitor.Start();
        monitor.Enqueue(new ActivityEvidence(ActivityEvidenceKind.ForegroundChanged));

        monitor.Stop();
        var exception = Record.Exception(() => monitor.Enqueue(new ActivityEvidence(ActivityEvidenceKind.SessionLocked)));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Null(exception);
        Assert.Equal(1, source.BuildCount);
    }

    private static ActivitySnapshot Snapshot(bool locked) =>
        new(locked, false, false, false, false, false, [], []);

    private sealed class MutableSnapshotBuilder : IActivitySnapshotBuilder
    {
        public ActivitySnapshot Snapshot { get; set; } = Snapshot(false);
        public int BuildCount { get; private set; }
        public int LastBuildThread { get; private set; }
        public Action<int>? OnBuild { get; set; }

        public ActivitySnapshot Build()
        {
            BuildCount++;
            LastBuildThread = Environment.CurrentManagedThreadId;
            OnBuild?.Invoke(BuildCount);
            return Snapshot;
        }
    }

    private sealed class QueuedCallbackTimeProvider : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            return new PassiveTimer();
        }

        public void FireQueuedCallback() => _callback!(_state);

        private sealed class PassiveTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class InlineZeroTimeProvider : TimeProvider
    {
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
            new InlineZeroTimer(callback, state);

        private sealed class InlineZeroTimer(TimerCallback callback, object? state) : ITimer
        {
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                if (dueTime == TimeSpan.Zero) callback(state);
                return true;
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Advance(elapsed);
        }

        private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _remaining = dueTime;
                _period = period;
                return true;
            }

            public void Advance(TimeSpan elapsed)
            {
                if (_disposed || _remaining == Timeout.InfiniteTimeSpan) return;
                _remaining -= elapsed;
                while (_remaining <= TimeSpan.Zero && !_disposed)
                {
                    callback(state);
                    if (_period == Timeout.InfiniteTimeSpan)
                    {
                        _remaining = Timeout.InfiniteTimeSpan;
                        return;
                    }

                    _remaining += _period;
                }
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
