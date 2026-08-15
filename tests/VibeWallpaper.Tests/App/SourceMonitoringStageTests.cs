using VibeWallpaper.App.Coordination;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Engine.Sources;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.App;

public sealed class SourceMonitoringStageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"source-stage-{Guid.NewGuid():N}.mp4");

    [Fact]
    public async Task StartAsync_ComposesMonitorAndManualPeriodicTickRevalidatesChangedActiveSource()
    {
        File.WriteAllBytes(_path, [1, 2, 3]);
        var store = new InMemoryStateStore();
        var probe = new RecordingProbe();
        var library = new WallpaperLibraryService(store, probe);
        var item = await library.ImportVideoAsync(_path, TestContext.Current.CancellationToken);
        store.Replace(WithAssignment(store.State, item.Definition.Id));
        var fallback = new FallbackRendererCoordinator(
            store.State, AppSettings.Default, new SuccessfulActivator());
        await fallback.InitializeAsync(_ => true, TestContext.Current.CancellationToken);
        var clock = new ManualTimeProvider();
        var changes = new SourceChangeMonitor(store);
        var active = new ActiveVideoSourceMonitor(
            store,
            changes,
            new VideoSourceRevalidator(store, library, fallback, _ => true),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5),
            clock);
        await using var stage = new SourceMonitoringStage(
            () => new SourceMonitoringServices(changes, active));
        await stage.StartAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(_path, "periodic-change", TestContext.Current.CancellationToken);
        probe.NextCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        clock.Advance(TimeSpan.FromSeconds(30));
        await probe.NextCall.Task.WaitAsync(
            TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);

        Assert.Equal(2, probe.CallCount);
    }

    [Fact]
    public async Task StartAsync_DoesNotWaitForSlowInitialActiveVideoProbe()
    {
        File.WriteAllBytes(_path, [1, 2, 3]);
        var store = new InMemoryStateStore();
        var probe = new RecordingProbe();
        var library = new WallpaperLibraryService(store, probe);
        var item = await library.ImportVideoAsync(_path, TestContext.Current.CancellationToken);
        store.Replace(WithAssignment(store.State, item.Definition.Id));
        var fallback = new FallbackRendererCoordinator(
            store.State, AppSettings.Default, new SuccessfulActivator());
        await fallback.InitializeAsync(_ => true, TestContext.Current.CancellationToken);
        var changes = new SourceChangeMonitor(store);
        var active = new ActiveVideoSourceMonitor(
            store,
            changes,
            new VideoSourceRevalidator(store, library, fallback, _ => true),
            TimeSpan.FromSeconds(30),
            TimeSpan.FromSeconds(5));
        await using var stage = new SourceMonitoringStage(
            () => new SourceMonitoringServices(changes, active));
        await File.AppendAllTextAsync(_path, "changed-before-start", TestContext.Current.CancellationToken);
        probe.Barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        probe.NextCall = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var start = stage.StartAsync(TestContext.Current.CancellationToken);
        await probe.NextCall.Task.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        var completedBeforeProbe = await Task.WhenAny(
            start,
            Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken)) == start;
        probe.Barrier.SetResult();
        await start;

        Assert.True(completedBeforeProbe);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private static PersistedState WithAssignment(PersistedState state, WallpaperId wallpaper)
    {
        var monitor = new MonitorIdentity("DISPLAY-A");
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var evidence = new MonitorIdentityEvidence(1, 1, 1, null, null, null, null, null, null, monitor.Key, bounds);
        var assignment = new WallpaperAssignment(
            new PersistedMonitorReference(monitor, evidence), wallpaper, DisplayMode.Independent,
            FitMode.Cover, 30, 0, null);
        return new PersistedState(state.SchemaVersion, state.Library, [assignment], state.Groups, state.AudioOwner);
    }

    private sealed class RecordingProbe : IVideoProbeService
    {
        public int CallCount { get; private set; }
        public TaskCompletionSource? NextCall { get; set; }
        public TaskCompletionSource? Barrier { get; set; }

        public async Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            NextCall?.TrySetResult();
            if (Barrier is not null) await Barrier.Task.WaitAsync(cancellationToken);
            return new VideoMetadata(320, 180, TimeSpan.FromSeconds(1), 30, "test", false);
        }
    }

    private sealed class SuccessfulActivator : IRuntimeWallpaperActivator
    {
        public Task ActivateAsync(
            MonitorIdentity output,
            WallpaperDefinition wallpaper,
            WallpaperAssignment persistedAssignment,
            long generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            _utcNow += elapsed;
            foreach (var timer in _timers.ToArray()) timer.Advance(elapsed);
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private TimeSpan _remaining = dueTime;
            private bool _disposed;

            public bool Change(TimeSpan due, TimeSpan repeat)
            {
                if (_disposed) return false;
                _remaining = due;
                period = repeat;
                return true;
            }

            public void Advance(TimeSpan elapsed)
            {
                if (_disposed || _remaining == Timeout.InfiniteTimeSpan) return;
                _remaining -= elapsed;
                if (_remaining > TimeSpan.Zero) return;
                callback(state);
                _remaining = period;
            }

            public void Dispose()
            {
                _disposed = true;
                owner._timers.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
