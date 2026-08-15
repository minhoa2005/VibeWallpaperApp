using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;
using VibeWallpaper.Engine.Runtime;
using System.Runtime.ExceptionServices;

namespace VibeWallpaper.Engine.Rendering.Video;

internal sealed class VideoRenderer : IWallpaperRenderer, IVideoSynchronizationEndpoint, IVideoAudioEndpoint
{
    private readonly IEngineDispatcher _dispatcher;
    private readonly ILibVlcRuntime _runtime;
    private readonly IVideoProbeService _probe;
    private readonly IVideoSurfaceWindowFactory _windows;
    private readonly VideoRendererOptions _options;
    private readonly IVideoPlaybackDiagnostics _diagnostics;
    private readonly TimeProvider _timeProvider;
    private IVideoResumeObserver? _resumeObserver;
    private readonly RendererStateMachine _state = new();
    private readonly RendererInstanceId _instanceId = RendererInstanceId.New();
    private IVideoSurfaceWindow? _surface;
    private ILibVlcPlayer? _player;
    private EventHandler? _endReachedHandler;
    private EventHandler<VideoFaultEventArgs>? _errorHandler;
    private EventHandler<VideoPlaybackProgressEventArgs>? _playbackProgressedHandler;
    private ITimer? _loopWatchdogTimer;
    private ITimer? _diagnosticsTimer;
    private MonitorIdentity? _output;
    private VideoPlaybackMetrics? _metrics;
    private NormalizedSourceRect _sourceCrop = new(0, 0, 1, 1);
    private int _loopRecoveryCount;
    private long _activeWatchdogSequence = -1;
    private long _nextWatchdogSequence;
    private long _lastProgressTimestamp = -1;
    private long _lastPlayerTimeMilliseconds = -1;
    private long _pendingLoopProgressTimestamp = -1;
    private long _pendingLoopPlayerTimeMilliseconds = -1;
    private long _loopGeneration;
    private long _generation;
    private bool _suppressEndReachedUntilNextDurationBoundary;
    private bool _awaitingLoopProgress;
    private bool _pendingLoopStartedByEndReached;
    private TimeSpan _duration;

    internal VideoRenderer(
        IEngineDispatcher dispatcher,
        ILibVlcRuntime runtime,
        IVideoProbeService probe,
        IVideoSurfaceWindowFactory windows,
        VideoRendererOptions options,
        IVideoResumeObserver? resumeObserver = null,
        IVideoPlaybackDiagnostics? diagnostics = null,
        TimeProvider? timeProvider = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _resumeObserver = resumeObserver;
        _diagnostics = diagnostics ?? LogSinkVideoPlaybackDiagnostics.None;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public RendererLifecycle Lifecycle => _state.Lifecycle;
    public PerformanceState PerformanceState => _state.PerformanceState;
    public RendererCapabilities Capabilities => RendererCapabilities.LibVlcFallback;
    public string Id => _instanceId.Value.ToString("D");
    public MonitorIdentity Output => _output ?? throw new InvalidOperationException("The renderer output is unavailable.");
    public TimeSpan Duration => _duration;
    public TimeSpan Position => TimeSpan.FromMilliseconds(Math.Max(0, RequiredPlayer().TimeMilliseconds));
    public bool IsConnected => _output is not null && Lifecycle is not RendererLifecycle.Disposed and not RendererLifecycle.Faulted;
    public bool IsActiveVideo => Lifecycle == RendererLifecycle.Active;
    public bool IsSuspended => PerformanceState == PerformanceState.Suspended;
    public int PersistedVolumePercent => _volumePercent;
    public bool IsMuted => RequiredPlayer().IsMuted;
    public int VolumePercent => RequiredPlayer().VolumePercent;

    /// <summary>No exact throttle rate is advertised until a measured native path exists.</summary>
    internal bool ExactThrottleFpsEnabled => false;

    public Task InitializeAsync(RendererContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        _state.TransitionTo(RendererLifecycle.Initializing);
        try
        {
            CaptureContextSettings(context);
            _output = context.Monitor.Identity;
            _metrics = new VideoPlaybackMetrics(Id, _output.Key, LibVlcRuntime.BackendName);
            _surface = _windows.Create(context.HostHwnd);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _state.TransitionTo(RendererLifecycle.Faulted);
            var durationMilliseconds = DiagnosticDurationMilliseconds();
            RecordPlaybackEvent("fault", FailureCodeFrom(exception), durationMilliseconds: durationMilliseconds);
            var cleanupFailure = CleanupOwnedResources(stopPlayer: true, releaseSurface: true);
            RecordCleanupFailure("fault", cleanupFailure, durationMilliseconds);
            throw;
        }
    }

    public async Task LoadAsync(WallpaperSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (source is not VideoSource video)
        {
            throw new ArgumentException("The video renderer requires a video source.", nameof(source));
        }

        _state.TransitionTo(RendererLifecycle.Loading);
        try
        {
            var metadata = await _probe.ProbeAsync(video.FilePath, cancellationToken);
            AssertEngineThread();
            _duration = metadata.Duration > TimeSpan.Zero ? metadata.Duration : TimeSpan.Zero;
            var surface = _surface ?? throw new InvalidOperationException("The video surface is unavailable.");
            var player = _runtime.CreatePlayer();
            _player = player;
            player.Hwnd = surface.Hwnd;
            player.VolumePercent = _volumePercent;
            // Task 12's application-wide audio policy is the only component allowed to unmute.
            player.IsMuted = true;
            player.ApplySourceCrop(_sourceCrop, metadata.Width, metadata.Height);
            player.Open(video.FilePath, VideoMediaOpenOptions.Wallpaper);
            AttachCallbacks(player);
            StartDiagnosticsTimer();
            RecordPlaybackEvent("open", durationMilliseconds: MediaDurationMilliseconds());
            _state.TransitionTo(RendererLifecycle.Ready);
        }
        catch (Exception exception)
        {
            _state.TransitionTo(RendererLifecycle.Faulted);
            var durationMilliseconds = DiagnosticDurationMilliseconds();
            RecordPlaybackEvent("open", FailureCodeFrom(exception), durationMilliseconds: durationMilliseconds);
            var cleanupFailure = CleanupOwnedResources(stopPlayer: true, releaseSurface: true);
            RecordCleanupFailure("fault", cleanupFailure, durationMilliseconds);
            throw;
        }
    }

    private int _volumePercent;

    public Task ActivateAsync(CancellationToken cancellationToken)
    {
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (Lifecycle == RendererLifecycle.Active)
        {
            return Task.CompletedTask;
        }

        if (Lifecycle != RendererLifecycle.Ready)
        {
            throw new InvalidOperationException($"Cannot activate a video renderer in state {Lifecycle}.");
        }

        try
        {
            if (!PlaybackMustBePaused(PerformanceState))
            {
                try
                {
                    RequiredPlayer().Play();
                    (_surface ?? throw new InvalidOperationException("The video surface is unavailable.")).Show();
                    RecordPlaybackEvent("play", durationMilliseconds: DiagnosticDurationMilliseconds());
                }
                catch (Exception exception)
                {
                    throw new VideoRendererControlException("activate", exception);
                }
            }

            _state.TransitionTo(RendererLifecycle.Active);
            return Task.CompletedTask;
        }
        catch (Exception exception)
        {
            _state.TransitionTo(RendererLifecycle.Faulted);
            var durationMilliseconds = DiagnosticDurationMilliseconds();
            RecordPlaybackEvent("play", FailureCodeFrom(exception), durationMilliseconds: durationMilliseconds);
            var cleanupFailure = CleanupOwnedResources(stopPlayer: true, releaseSurface: true);
            RecordCleanupFailure("fault", cleanupFailure, durationMilliseconds);
            throw;
        }
    }

    public Task ApplyPerformanceAsync(RendererPerformanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        var previous = PerformanceState;
        if (previous == request.State)
        {
            return Task.CompletedTask;
        }

        if (Lifecycle != RendererLifecycle.Active)
        {
            _state.SetPerformanceState(request.State);
            return Task.CompletedTask;
        }

        var wasPaused = PlaybackMustBePaused(previous);
        var mustPause = PlaybackMustBePaused(request.State);
        if (!wasPaused && mustPause)
        {
            try
            {
                RequiredPlayer().Pause();
            }
            catch (Exception exception)
            {
                throw new VideoRendererControlException("pause", exception);
            }
        }
        else if (wasPaused && !mustPause)
        {
            // Task 12 extends this resume point with group-clock resynchronization.
            try
            {
                RequiredPlayer().Play();
                _resumeObserver?.NotifyResumed(Id);
            }
            catch (Exception exception)
            {
                throw new VideoRendererControlException("resume", exception);
            }
        }

        _state.SetPerformanceState(request.State);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (Lifecycle is RendererLifecycle.Stopped or RendererLifecycle.Disposed)
        {
            return Task.CompletedTask;
        }

        try
        {
            var durationMilliseconds = DiagnosticDurationMilliseconds();
            var cleanupFailure = CleanupOwnedResources(stopPlayer: true, releaseSurface: false);
            RecordPlaybackEvent("stop", cleanupFailure is null ? null : FailureCodeFrom(cleanupFailure), durationMilliseconds: durationMilliseconds);
            if (cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
        finally
        {
            _state.Stop();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Lifecycle == RendererLifecycle.Disposed)
        {
            return ValueTask.CompletedTask;
        }

        return _dispatcher.HasThreadAccess
            ? DisposeOnEngineThread()
            : new ValueTask(_dispatcher.InvokeAsync(_ => DisposeOnEngineThread()));
    }

    private ValueTask DisposeOnEngineThread()
    {
        AssertEngineThread();
        if (Lifecycle == RendererLifecycle.Disposed)
        {
            return ValueTask.CompletedTask;
        }

        try
        {
            var durationMilliseconds = DiagnosticDurationMilliseconds();
            var cleanupFailure = CleanupOwnedResources(stopPlayer: true, releaseSurface: true);
            RecordPlaybackEvent("dispose", cleanupFailure is null ? null : FailureCodeFrom(cleanupFailure), durationMilliseconds: durationMilliseconds);
            if (cleanupFailure is not null)
            {
                ExceptionDispatchInfo.Capture(cleanupFailure).Throw();
            }
        }
        finally
        {
            _state.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    internal void SetValidatedVolume(int volumePercent)
    {
        if (volumePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(volumePercent));
        }

        _volumePercent = volumePercent;
    }

    public void Seek(TimeSpan position)
    {
        AssertEngineThread();
        if (position < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(position));
        RequiredPlayer().TimeMilliseconds = checked((long)position.TotalMilliseconds);
    }

    public void SetMuted(bool muted)
    {
        AssertEngineThread();
        RequiredPlayer().IsMuted = muted;
    }

    public void SetVolume(int volumePercent)
    {
        AssertEngineThread();
        SetValidatedVolume(volumePercent);
        RequiredPlayer().VolumePercent = volumePercent;
    }

    public void AttachResumeObserver(IVideoResumeObserver observer)
    {
        AssertEngineThread();
        _resumeObserver = observer ?? throw new ArgumentNullException(nameof(observer));
    }

    private void AttachCallbacks(ILibVlcPlayer player)
    {
        var output = _output ?? throw new InvalidOperationException("The renderer output is unavailable.");
        var generation = checked(++_generation);
        var instance = _instanceId;
        _endReachedHandler = (_, _) => PostNativeCallback(
            new NativeCallbackEvent(instance, output, generation, NativeCallbackKind.MediaEnded));
        _errorHandler = (_, error) => PostNativeCallback(
            new NativeCallbackEvent(
                instance, output, generation, NativeCallbackKind.Faulted, error.FaultCode, error.Message));
        _playbackProgressedHandler = (_, progress) => PostNativeCallback(
            new NativeCallbackEvent(
                instance,
                output,
                generation,
                NativeCallbackKind.PlaybackProgressed,
                PlaybackTimeMilliseconds: progress.TimeMilliseconds,
                ProgressTimestamp: _timeProvider.GetTimestamp()));
        player.EndReached += _endReachedHandler;
        player.EncounteredError += _errorHandler;
        player.PlaybackProgressed += _playbackProgressedHandler;
    }

    private void PostNativeCallback(NativeCallbackEvent callback)
    {
        try
        {
            _ = ObserveCallbackAsync(
                _dispatcher.InvokeAsync(token => HandleNativeCallbackAsync(callback, token)),
                callback);
        }
        catch (ObjectDisposedException)
        {
            // Shutdown won the callback race; immutable callback data is safe to discard.
        }
    }

    private ValueTask HandleNativeCallbackAsync(NativeCallbackEvent callback, CancellationToken cancellationToken)
    {
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (callback.RendererInstance != _instanceId || callback.Generation != _generation || _player is null)
        {
            return ValueTask.CompletedTask;
        }

        if (callback.Kind == NativeCallbackKind.Faulted)
        {
            var durationMilliseconds = DiagnosticDurationMilliseconds();
            RecordPlaybackEvent("fault", callback.FaultCode, durationMilliseconds: durationMilliseconds);
            _state.TransitionTo(RendererLifecycle.Faulted);
            var cleanupFailure = CleanupOwnedResources(stopPlayer: true, releaseSurface: true);
            RecordCleanupFailure("fault", cleanupFailure, durationMilliseconds);
            return ValueTask.CompletedTask;
        }

        if (callback.Kind == NativeCallbackKind.PlaybackProgressed)
        {
            ObservePlaybackProgress(callback);
            return ValueTask.CompletedTask;
        }

        if (callback.Kind == NativeCallbackKind.MediaEnded)
        {
            HandleMediaEnded(callback);
            return ValueTask.CompletedTask;
        }

        if (callback.Kind == NativeCallbackKind.LoopWatchdogExpired)
        {
            HandleLoopWatchdogExpired(callback);
        }

        return ValueTask.CompletedTask;
    }

    private void ObservePlaybackProgress(NativeCallbackEvent callback)
    {
        var previousPlayerTimeMilliseconds = _lastPlayerTimeMilliseconds;
        var currentPlayerTimeMilliseconds = callback.PlaybackTimeMilliseconds;
        _lastProgressTimestamp = callback.ProgressTimestamp;
        _lastPlayerTimeMilliseconds = currentPlayerTimeMilliseconds;
        if (currentPlayerTimeMilliseconds == previousPlayerTimeMilliseconds)
        {
            _metrics?.RecordRepeated();
        }
        else
        {
            _metrics?.RecordPresented();
        }

        if (currentPlayerTimeMilliseconds >= 0 &&
            currentPlayerTimeMilliseconds != previousPlayerTimeMilliseconds &&
            _loopRecoveryCount > 0)
        {
            _loopRecoveryCount = 0;
        }

        var durationMilliseconds = MediaDurationMilliseconds();
        var nearDurationBoundary = IsNearDurationBoundary(currentPlayerTimeMilliseconds, durationMilliseconds);
        if (nearDurationBoundary)
        {
            _suppressEndReachedUntilNextDurationBoundary = false;
        }

        if (IsDurationBoundaryWrap(previousPlayerTimeMilliseconds, currentPlayerTimeMilliseconds, durationMilliseconds))
        {
            ConfirmLoopProgress(currentPlayerTimeMilliseconds, confirmedByTimestampWrap: true);
            return;
        }

        if (_awaitingLoopProgress)
        {
            if (_pendingLoopStartedByEndReached)
            {
                if (currentPlayerTimeMilliseconds >= 0 &&
                    currentPlayerTimeMilliseconds != _pendingLoopPlayerTimeMilliseconds)
                {
                    ConfirmLoopProgress(currentPlayerTimeMilliseconds, confirmedByTimestampWrap: false);
                }

                return;
            }

            if (currentPlayerTimeMilliseconds < 0 ||
                currentPlayerTimeMilliseconds == _pendingLoopPlayerTimeMilliseconds)
            {
                return;
            }

            ClearPendingLoopProgress(cancelWatchdog: true);
        }

        if (nearDurationBoundary)
        {
            ArmDurationBoundaryWatchdog(callback, currentPlayerTimeMilliseconds, durationMilliseconds);
        }
    }

    private void HandleMediaEnded(NativeCallbackEvent callback)
    {
        if (Lifecycle != RendererLifecycle.Active || PlaybackMustBePaused(PerformanceState))
        {
            return;
        }

        if (_suppressEndReachedUntilNextDurationBoundary)
        {
            return;
        }

        _awaitingLoopProgress = true;
        _pendingLoopStartedByEndReached = true;
        _pendingLoopProgressTimestamp = _lastProgressTimestamp;
        _pendingLoopPlayerTimeMilliseconds = _lastPlayerTimeMilliseconds;
        RecordPlaybackEvent("native-end", durationMilliseconds: DiagnosticDurationMilliseconds());
        ArmLoopWatchdog(callback, TimeSpan.FromSeconds(1));
    }

    private void HandleLoopWatchdogExpired(NativeCallbackEvent callback)
    {
        if (!_awaitingLoopProgress ||
            callback.ProgressTimestamp != _pendingLoopProgressTimestamp ||
            callback.WatchdogSequence != _activeWatchdogSequence)
        {
            return;
        }

        if (_lastPlayerTimeMilliseconds >= 0 &&
            _lastPlayerTimeMilliseconds != _pendingLoopPlayerTimeMilliseconds)
        {
            ClearPendingLoopProgress(cancelWatchdog: false);
            return;
        }

        if (_loopRecoveryCount == 0)
        {
            try
            {
                var player = RequiredPlayer();
                player.TimeMilliseconds = 0;
                player.Play();
                _metrics?.RecordRecovery();
                _loopRecoveryCount = 1;
                ClearPendingLoopProgress(cancelWatchdog: true);
                RecordPlaybackEvent("loop-recovery", retryCount: 1, durationMilliseconds: 0);
            }
            catch (Exception exception)
            {
                _state.TransitionTo(RendererLifecycle.Faulted);
                var durationMilliseconds = DiagnosticDurationMilliseconds();
                RecordPlaybackEvent("fault", FailureCodeFrom(exception), retryCount: 1, durationMilliseconds: durationMilliseconds);
                var cleanupFailure = CleanupOwnedResources(stopPlayer: true, releaseSurface: true);
                RecordCleanupFailure("fault", cleanupFailure, durationMilliseconds, retryCount: 1);
            }

            return;
        }

        _state.TransitionTo(RendererLifecycle.Faulted);
        var exhaustedDurationMilliseconds = DiagnosticDurationMilliseconds();
        RecordPlaybackEvent("fault", "loop.recovery.exhausted", retryCount: _loopRecoveryCount, durationMilliseconds: exhaustedDurationMilliseconds);
        var failure = CleanupOwnedResources(stopPlayer: true, releaseSurface: true);
        RecordCleanupFailure("fault", failure, exhaustedDurationMilliseconds, retryCount: _loopRecoveryCount);
    }

    private void ConfirmLoopProgress(long durationMilliseconds, bool confirmedByTimestampWrap)
    {
        var nextLoopGeneration = checked(++_loopGeneration);
        _metrics?.RecordLoop(nextLoopGeneration);
        _suppressEndReachedUntilNextDurationBoundary = confirmedByTimestampWrap;
        _loopRecoveryCount = 0;
        ClearPendingLoopProgress(cancelWatchdog: true);
        RecordPlaybackEvent("loop-progress", durationMilliseconds: Math.Max(0, durationMilliseconds));
    }

    private void ClearPendingLoopProgress(bool cancelWatchdog)
    {
        _awaitingLoopProgress = false;
        _pendingLoopStartedByEndReached = false;
        _pendingLoopProgressTimestamp = -1;
        _pendingLoopPlayerTimeMilliseconds = -1;
        if (cancelWatchdog)
        {
            CancelLoopWatchdog();
        }
    }

    private void ArmDurationBoundaryWatchdog(NativeCallbackEvent callback, long playerTimeMilliseconds, long durationMilliseconds)
    {
        var remainingMilliseconds = Math.Max(0, durationMilliseconds - playerTimeMilliseconds);
        _awaitingLoopProgress = true;
        _pendingLoopStartedByEndReached = false;
        _pendingLoopProgressTimestamp = callback.ProgressTimestamp;
        _pendingLoopPlayerTimeMilliseconds = playerTimeMilliseconds;
        ArmLoopWatchdog(callback, TimeSpan.FromMilliseconds(remainingMilliseconds) + TimeSpan.FromSeconds(1));
    }

    private void ArmLoopWatchdog(NativeCallbackEvent callback, TimeSpan dueTime)
    {
        CancelLoopWatchdog();
        var instance = callback.RendererInstance;
        var output = callback.Output;
        var generation = callback.Generation;
        var progressTimestamp = _pendingLoopProgressTimestamp;
        var watchdogSequence = checked(++_nextWatchdogSequence);
        _activeWatchdogSequence = watchdogSequence;
        _loopWatchdogTimer = _timeProvider.CreateTimer(
            _ => PostNativeCallback(
                new NativeCallbackEvent(
                    instance,
                    output,
                    generation,
                    NativeCallbackKind.LoopWatchdogExpired,
                    ProgressTimestamp: progressTimestamp,
                    WatchdogSequence: watchdogSequence)),
            null,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }

    private void CancelLoopWatchdog()
    {
        var timer = _loopWatchdogTimer;
        _loopWatchdogTimer = null;
        _activeWatchdogSequence = -1;
        timer?.Dispose();
    }

    private async Task ObserveCallbackAsync(Task callbackTask, NativeCallbackEvent callback)
    {
        try
        {
            await callbackTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            RecordPlaybackEvent(
                "fault",
                FailureCodeFrom(exception),
                retryCount: callback.Kind == NativeCallbackKind.LoopWatchdogExpired ? Math.Max(1, _loopRecoveryCount) : 0,
                durationMilliseconds: DiagnosticDurationMilliseconds());
        }
    }

    private bool PlaybackMustBePaused(PerformanceState state) =>
        state == PerformanceState.Suspended ||
        (state == PerformanceState.Throttled && _options.SuspendWhenThrottled);

    private ILibVlcPlayer RequiredPlayer() =>
        _player ?? throw new InvalidOperationException("The video player is unavailable.");

    private Exception? CleanupOwnedResources(bool stopPlayer, bool releaseSurface)
    {
        Exception? failure = null;
        _awaitingLoopProgress = false;
        _pendingLoopStartedByEndReached = false;
        _suppressEndReachedUntilNextDurationBoundary = false;
        _loopRecoveryCount = 0;
        _activeWatchdogSequence = -1;
        _pendingLoopProgressTimestamp = -1;
        _pendingLoopPlayerTimeMilliseconds = -1;
        _lastProgressTimestamp = -1;
        _lastPlayerTimeMilliseconds = -1;
        _loopGeneration = 0;
        _duration = TimeSpan.Zero;
        StopDiagnosticsTimer();
        var watchdog = _loopWatchdogTimer;
        _loopWatchdogTimer = null;
        if (watchdog is not null)
        {
            CaptureCleanupFailure(ref failure, watchdog.Dispose);
        }

        var player = _player;
        _player = null;
        if (player is not null)
        {
            checked { _generation++; }
            var endReachedHandler = _endReachedHandler;
            var errorHandler = _errorHandler;
            var playbackProgressedHandler = _playbackProgressedHandler;
            _endReachedHandler = null;
            _errorHandler = null;
            _playbackProgressedHandler = null;

            if (endReachedHandler is not null)
            {
                CaptureCleanupFailure(ref failure, () => player.EndReached -= endReachedHandler);
            }

            if (errorHandler is not null)
            {
                CaptureCleanupFailure(ref failure, () => player.EncounteredError -= errorHandler);
            }

            if (playbackProgressedHandler is not null)
            {
                CaptureCleanupFailure(ref failure, () => player.PlaybackProgressed -= playbackProgressedHandler);
            }

            if (stopPlayer)
            {
                CaptureCleanupFailure(ref failure, player.Stop);
            }

            CaptureCleanupFailure(ref failure, player.Dispose);
        }

        if (releaseSurface)
        {
            var surface = _surface;
            _surface = null;
            if (surface is not null)
            {
                CaptureCleanupFailure(ref failure, surface.Dispose);
            }
        }

        return failure;
    }

    private static void CaptureCleanupFailure(ref Exception? failure, Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception)
        {
            failure ??= exception;
        }
    }

    private void AssertEngineThread()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("Video renderer control and HWND operations must run on the engine thread.");
        }
    }

    private void CaptureContextSettings(RendererContext context)
    {
        SetValidatedVolume(context.Settings.VolumePercent);
        _sourceCrop = context.SourceCrop;
    }

    private void StartDiagnosticsTimer()
    {
        StopDiagnosticsTimer();
        _diagnosticsTimer = _timeProvider.CreateTimer(
            _ => FlushMetrics(),
            null,
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(10));
    }

    private void StopDiagnosticsTimer()
    {
        var timer = _diagnosticsTimer;
        _diagnosticsTimer = null;
        timer?.Dispose();
    }

    private void FlushMetrics()
    {
        var metrics = _metrics;
        if (metrics is null)
        {
            return;
        }

        _diagnostics.Record(metrics.Snapshot());
    }

    private void RecordCleanupFailure(string operation, Exception? failure, long durationMilliseconds, int retryCount = 0)
    {
        if (failure is null)
        {
            return;
        }

        RecordPlaybackEvent(operation, FailureCodeFrom(failure), retryCount, durationMilliseconds);
    }

    private void RecordPlaybackEvent(
        string operation,
        string? failureCode = null,
        int retryCount = 0,
        long? durationMilliseconds = null)
    {
        var output = _output;
        if (output is null)
        {
            return;
        }

        _diagnostics.Record(new VideoPlaybackEvent(
            operation,
            Id,
            output.Key,
            LibVlcRuntime.BackendName,
            failureCode,
            retryCount,
            Math.Max(0, durationMilliseconds ?? DiagnosticDurationMilliseconds())));
    }

    private long DiagnosticDurationMilliseconds()
    {
        if (_lastPlayerTimeMilliseconds >= 0)
        {
            return _lastPlayerTimeMilliseconds;
        }

        return MediaDurationMilliseconds();
    }

    private long MediaDurationMilliseconds() =>
        _duration > TimeSpan.Zero
            ? checked((long)_duration.TotalMilliseconds)
            : 0;

    private static bool IsDurationBoundaryWrap(long previousMs, long currentMs, long durationMs)
    {
        if (previousMs < 0 || currentMs < 0 || durationMs <= 0) return false;
        var boundaryWindowMs = Math.Clamp(durationMs / 5, 250, 2_000);
        return previousMs >= durationMs - boundaryWindowMs && currentMs <= boundaryWindowMs;
    }

    private static bool IsNearDurationBoundary(long currentMs, long durationMs)
    {
        if (currentMs < 0 || durationMs <= 0) return false;
        var boundaryWindowMs = Math.Clamp(durationMs / 5, 250, 2_000);
        return currentMs >= durationMs - boundaryWindowMs;
    }

    private static string FailureCodeFrom(Exception exception) =>
        exception switch
        {
            VideoRendererControlException { InnerException: { } innerException } => innerException.GetType().Name,
            _ => exception.GetType().Name,
        };
}
