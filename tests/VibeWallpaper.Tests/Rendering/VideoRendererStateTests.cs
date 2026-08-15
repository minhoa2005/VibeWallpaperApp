using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Rendering;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;
using VibeWallpaper.Engine.Rendering.Solid;
using VibeWallpaper.Engine.Rendering.Video;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Rendering;

public sealed class VideoRendererStateTests
{
    [Fact]
    public async Task Activate_ShowsVideoSurface()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeVideoSurfaceWindowFactory();
        var renderer = new VideoRenderer(
            dispatcher,
            new FakeLibVlcRuntime(),
            new FakeVideoProbeService(),
            windows,
            VideoRendererOptions.Default);

        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(), token);
            await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
            await renderer.ActivateAsync(token);
        }, TestContext.Current.CancellationToken);

        Assert.True(windows.IsVisible);
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task Lifecycle_InitializeLoadActivate_ConfiguresOneMutedPlayerAndReachesActive()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var runtime = new FakeLibVlcRuntime();
        var windows = new FakeVideoSurfaceWindowFactory();
        var renderer = new VideoRenderer(dispatcher, runtime, new FakeVideoProbeService(), windows, VideoRendererOptions.Default);

        Assert.Equal(RendererLifecycle.Created, renderer.Lifecycle);
        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(volume: 37), token);
            Assert.Equal(RendererLifecycle.Initializing, renderer.Lifecycle);
            await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
            Assert.Equal(RendererLifecycle.Ready, renderer.Lifecycle);
            await renderer.ActivateAsync(token);
        }, TestContext.Current.CancellationToken);

        var player = Assert.Single(runtime.Players);
        Assert.Equal(RendererLifecycle.Active, renderer.Lifecycle);
        Assert.Equal(windows.CreatedHwnd, player.AssignedHwnd);
        Assert.Equal(VideoPath, player.OpenedPath);
        Assert.Equal(37, player.VolumePercent);
        Assert.True(player.IsMuted);
        Assert.Equal(1, player.PlayCount);
        Assert.True(runtime.HardwareDecodingRequested);
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task Load_SpanSourceCropIsAppliedBeforeMediaOpenAndPlayback()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var runtime = new FakeLibVlcRuntime();
        var renderer = new VideoRenderer(
            dispatcher, runtime, new FakeVideoProbeService(), new FakeVideoSurfaceWindowFactory(), VideoRendererOptions.Default);
        var crop = new NormalizedSourceRect(0.5, 0, 0.5, 1);

        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(sourceCrop: crop), token);
            await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
            await renderer.ActivateAsync(token);
        }, TestContext.Current.CancellationToken);

        var player = Assert.Single(runtime.Players);
        Assert.Equal((crop, 1920, 1080), player.AppliedSourceCrop);
        Assert.Equal(["crop", "open", "play"], player.Operations.Where(operation => operation is "crop" or "open" or "play"));
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task ActivateBeforeReady_RejectsInvalidTransitionWithoutCreatingAPlayer()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var runtime = new FakeLibVlcRuntime();
        var renderer = new VideoRenderer(
            dispatcher, runtime, new FakeVideoProbeService(), new FakeVideoSurfaceWindowFactory(), VideoRendererOptions.Default);

        await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(
            token => new ValueTask(renderer.ActivateAsync(token)), TestContext.Current.CancellationToken));

        Assert.Empty(runtime.Players);
        Assert.Equal(RendererLifecycle.Created, renderer.Lifecycle);
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task SuspendedAndRunning_PauseAndResumeOnlyOnceWhileActive()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();

        await fixture.Dispatcher.InvokeAsync(async token =>
        {
            await fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token);
            await fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token);
            await fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token);
            await fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Player.PauseCount);
        Assert.Equal(2, fixture.Player.PlayCount);
        Assert.Equal(PerformanceState.Running, fixture.Renderer.PerformanceState);
    }

    [Fact]
    public async Task Resume_NotifiesGroupSynchronizerOnceAfterNativePlaybackRestarts()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var runtime = new FakeLibVlcRuntime();
        var observer = new RecordingResumeObserver();
        var renderer = new VideoRenderer(
            dispatcher,
            runtime,
            new FakeVideoProbeService(),
            new FakeVideoSurfaceWindowFactory(),
            VideoRendererOptions.Default,
            observer);
        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(), token);
            await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
            await renderer.ActivateAsync(token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(renderer.Id, Assert.Single(observer.ResumedIds));
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task PauseFailure_DoesNotPublishSuspendedAndAllowsRetry()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();
        fixture.Player.PauseFailuresRemaining = 1;

        var error = await Assert.ThrowsAsync<VideoRendererControlException>(() => fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token)),
            TestContext.Current.CancellationToken));

        Assert.Equal("pause", error.Operation);
        Assert.Equal(PerformanceState.Running, fixture.Renderer.PerformanceState);
        Assert.Equal(RendererLifecycle.Active, fixture.Renderer.Lifecycle);
        await fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PerformanceState.Suspended, fixture.Renderer.PerformanceState);
        Assert.Equal(2, fixture.Player.PauseCount);
    }

    [Fact]
    public async Task ResumeFailure_DoesNotPublishRunningAndAllowsRetry()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();
        await fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token)),
            TestContext.Current.CancellationToken);
        fixture.Player.PlayFailuresRemaining = 1;

        var error = await Assert.ThrowsAsync<VideoRendererControlException>(() => fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token)),
            TestContext.Current.CancellationToken));

        Assert.Equal("resume", error.Operation);
        Assert.Equal(PerformanceState.Suspended, fixture.Renderer.PerformanceState);
        Assert.Equal(RendererLifecycle.Active, fixture.Renderer.Lifecycle);
        await fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token)),
            TestContext.Current.CancellationToken);
        Assert.Equal(PerformanceState.Running, fixture.Renderer.PerformanceState);
        Assert.Equal(3, fixture.Player.PlayCount);
    }

    [Fact]
    public async Task RunningBeforeActivation_DoesNotStartPlayback()
    {
        await using var fixture = await RendererFixture.CreateReadyAsync();

        await fixture.Dispatcher.InvokeAsync(async token =>
        {
            await fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token);
            await fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(0, fixture.Player.PlayCount);
        Assert.Equal(0, fixture.Player.PauseCount);
    }

    [Fact]
    public async Task ThrottledWithoutMeasuredPath_ContinuesPlaybackWithoutClaimingFrameRateControl()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();

        await fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled), token)),
            TestContext.Current.CancellationToken);

        Assert.Equal(PerformanceState.Throttled, fixture.Renderer.PerformanceState);
        Assert.Equal(0, fixture.Player.PauseCount);
        Assert.Equal(1, fixture.Player.PlayCount);
        Assert.False(fixture.Renderer.ExactThrottleFpsEnabled);
    }

    [Fact]
    public async Task ThrottledWithSuspendMapping_PausesAndRunningResumes()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var runtime = new FakeLibVlcRuntime();
        var renderer = new VideoRenderer(
            dispatcher,
            runtime,
            new FakeVideoProbeService(),
            new FakeVideoSurfaceWindowFactory(),
            new VideoRendererOptions(suspendWhenThrottled: true));
        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(), token);
            await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
            await renderer.ActivateAsync(token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled), token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token);
        }, TestContext.Current.CancellationToken);

        var player = Assert.Single(runtime.Players);
        Assert.Equal(1, player.PauseCount);
        Assert.Equal(2, player.PlayCount);
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task EndWithoutProgress_RecoversOnceThenFaults()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await RendererFixture.CreateActiveAsync(time);

        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Player.PlayCount);
        Assert.Equal(RendererLifecycle.Faulted, fixture.Renderer.Lifecycle);
    }

    [Fact]
    public async Task EndReached_DoesNotReplayImmediatelyAndProgressCancelsPendingWatchdog()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await RendererFixture.CreateActiveAsync(time);

        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Player.PlayCount);

        fixture.Player.RaisePlaybackProgressed(125);
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, fixture.Player.PlayCount);
        Assert.Equal(RendererLifecycle.Active, fixture.Renderer.Lifecycle);
    }

    [Fact]
    public async Task EndReached_DuplicateTimestampDoesNotCancelPendingWatchdog()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await RendererFixture.CreateActiveAsync(time);

        fixture.Player.RaisePlaybackProgressed(1_000);
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(500));
        fixture.Player.RaisePlaybackProgressed(1_000);
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromMilliseconds(500));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Player.PlayCount);
        Assert.Equal(RendererLifecycle.Active, fixture.Renderer.Lifecycle);
    }

    [Fact]
    public async Task TimestampWrap_WithoutEndReached_IncrementsOneLoopAndRecordsProgress()
    {
        var time = new ManualTimeProvider();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(time, diagnostics);

        fixture.Player.RaisePlaybackProgressed(900);
        fixture.Player.RaisePlaybackProgressed(100);
        fixture.Player.RaisePlaybackProgressed(420);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, diagnostics.Snapshots[^1].LoopGeneration);
        Assert.Single(diagnostics.Events, entry => entry.Operation == "loop-progress");
        Assert.Equal(1, fixture.Player.PlayCount);
    }

    [Fact]
    public async Task LateEndReached_AfterTimestampWrap_DoesNotDoubleCountLoop()
    {
        var time = new ManualTimeProvider();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(time, diagnostics);

        fixture.Player.RaisePlaybackProgressed(900);
        fixture.Player.RaisePlaybackProgressed(100);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaisePlaybackProgressed(420);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(1, diagnostics.Snapshots[^1].LoopGeneration);
        Assert.Single(diagnostics.Events, entry => entry.Operation == "loop-progress");
        Assert.DoesNotContain(diagnostics.Events, entry => entry.Operation == "native-end");
        Assert.Equal(1, fixture.Player.PlayCount);
    }

    [Fact]
    public async Task LateEndReached_AfterPostWrapProgress_DoesNotBlockNextLegitimateLoop()
    {
        var time = new ManualTimeProvider();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(time, diagnostics);

        fixture.Player.RaisePlaybackProgressed(900);
        fixture.Player.RaisePlaybackProgressed(100);
        fixture.Player.RaisePlaybackProgressed(420);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        fixture.Player.RaiseEndReached();
        fixture.Player.RaisePlaybackProgressed(500);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        fixture.Player.RaisePlaybackProgressed(900);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaiseEndReached();
        fixture.Player.RaisePlaybackProgressed(100);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(2, diagnostics.Snapshots[^1].LoopGeneration);
        Assert.Equal(2, diagnostics.Events.Count(entry => entry.Operation == "loop-progress"));
        Assert.Single(diagnostics.Events, entry => entry.Operation == "native-end");
        Assert.Equal(1, fixture.Player.PlayCount);
    }

    [Fact]
    public async Task BackwardSeek_AwayFromDurationBoundary_DoesNotCountAsLoop()
    {
        var time = new ManualTimeProvider();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(time, diagnostics);

        fixture.Player.RaisePlaybackProgressed(700);
        fixture.Player.RaisePlaybackProgressed(300);
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(0, diagnostics.Snapshots[^1].LoopGeneration);
        Assert.DoesNotContain(diagnostics.Events, entry => entry.Operation == "loop-progress");
    }

    [Fact]
    public async Task PlaybackProgressAfterRecovery_ResetsRecoveryBudget()
    {
        var time = new ManualTimeProvider();
        await using var fixture = await RendererFixture.CreateActiveAsync(time);

        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(2, fixture.Player.PlayCount);

        fixture.Player.RaisePlaybackProgressed(250);
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(3, fixture.Player.PlayCount);
        Assert.Equal(RendererLifecycle.Active, fixture.Renderer.Lifecycle);
    }

    [Fact]
    public async Task LoopCallbacks_RecordNativeEndProgressAndRecoveryDiagnostics()
    {
        var time = new ManualTimeProvider();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(time, diagnostics);

        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaisePlaybackProgressed(125);
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        fixture.Player.RaiseEndReached();
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(1));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Contains(diagnostics.Events, static entry => entry.Operation == "native-end");
        Assert.Contains(diagnostics.Events, static entry => entry.Operation == "loop-progress");
        var recovery = Assert.Single(diagnostics.Events, static entry => entry.Operation == "loop-recovery");
        Assert.Equal(1, recovery.RetryCount);
    }

    [Fact]
    public async Task NativeFault_PostsToEngineBeforeFaultingAndReleasingThePlayer()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();

        fixture.Player.RaiseError("decoder.failed", "hardware decoder stopped");
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(RendererLifecycle.Faulted, fixture.Renderer.Lifecycle);
        Assert.Equal(1, fixture.Player.StopCount);
        Assert.Equal(1, fixture.Player.DisposeCount);
    }

    [Fact]
    public async Task NativeFault_RecordsBoundedFaultDiagnostic()
    {
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(diagnostics: diagnostics);

        fixture.Player.RaiseError("decoder.failed", "hardware decoder stopped");
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        var fault = Assert.Single(diagnostics.Events, static entry => entry.Operation == "fault");
        Assert.Equal(fixture.Renderer.Id, fault.RendererId);
        Assert.Equal("DISPLAY-VIDEO", fault.OutputKey);
        Assert.Equal("libvlc", fault.Backend);
        Assert.Equal("decoder.failed", fault.FailureCode);
    }

    [Fact]
    public async Task NativeFault_DoesNotConvertFaultIntoDroppedFrameMetric()
    {
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(diagnostics: diagnostics);

        fixture.Player.RaiseError("decoder.failed", "hardware decoder stopped");
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        VideoRendererReflectionProbe.InvokePrivateFlushMetrics(fixture.Renderer);

        var snapshot = Assert.Single(diagnostics.Snapshots);
        Assert.Equal(0, snapshot.DroppedFrames);
    }

    [Fact]
    public async Task Progress_DoesNotReportRequestedHardwareDecodeAsConfirmed()
    {
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(diagnostics: diagnostics);

        fixture.Player.RaisePlaybackProgressed(125);
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        VideoRendererReflectionProbe.InvokePrivateFlushMetrics(fixture.Renderer);

        var snapshot = Assert.Single(diagnostics.Snapshots);
        Assert.False(snapshot.HardwareDecodeConfirmed);
    }

    [Fact]
    public async Task ActivationFailure_DetachesCallbacksAndDisposesOwnedNativeResourcesOnce()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var runtime = new FakeLibVlcRuntime { FailPlay = true };
        var windows = new FakeVideoSurfaceWindowFactory();
        var renderer = new VideoRenderer(dispatcher, runtime, new FakeVideoProbeService(), windows, VideoRendererOptions.Default);
        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(), token);
            await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
        }, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<VideoRendererControlException>(() => dispatcher.InvokeAsync(
            token => new ValueTask(renderer.ActivateAsync(token)), TestContext.Current.CancellationToken));

        var player = Assert.Single(runtime.Players);
        Assert.Equal(RendererLifecycle.Faulted, renderer.Lifecycle);
        Assert.Equal(1, player.StopCount);
        Assert.Equal(1, player.DisposeCount);
        Assert.True(player.CallbacksRemovedBeforeDispose);
        Assert.Equal(1, windows.DisposeCount);
        await renderer.DisposeAsync();
        Assert.Equal(1, player.DisposeCount);
        Assert.Equal(1, windows.DisposeCount);
    }

    [Fact]
    public async Task LoadFailure_PreservesOpenErrorWhileAttemptingPlayerAndSurfaceCleanup()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var runtime = new FakeLibVlcRuntime();
        var windows = new FakeVideoSurfaceWindowFactory { DisposeFailuresRemaining = 1 };
        var renderer = new VideoRenderer(dispatcher, runtime, new FakeVideoProbeService(), windows, VideoRendererOptions.Default);
        await dispatcher.InvokeAsync(
            token => new ValueTask(renderer.InitializeAsync(Context(), token)), TestContext.Current.CancellationToken);
        runtime.NextPlayerConfiguration = player =>
        {
            player.OpenFailuresRemaining = 1;
            player.StopFailuresRemaining = 1;
            player.DisposeFailuresRemaining = 1;
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => dispatcher.InvokeAsync(
            token => new ValueTask(renderer.LoadAsync(VideoSource.Create(VideoPath), token)),
            TestContext.Current.CancellationToken));

        var player = Assert.Single(runtime.Players);
        Assert.Equal("open failed", error.Message);
        Assert.Equal(1, player.StopCount);
        Assert.Equal(1, player.DisposeCount);
        Assert.Equal(1, windows.DisposeCount);
        Assert.Equal(RendererLifecycle.Faulted, renderer.Lifecycle);
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task ActivationFailure_PreservesTypedPlayFaultWhileAllCleanupStepsThrow()
    {
        await using var fixture = await RendererFixture.CreateReadyAsync();
        fixture.Player.PlayFailuresRemaining = 1;
        fixture.Player.ThrowOnEndRemoval = true;
        fixture.Player.ThrowOnErrorRemoval = true;
        fixture.Player.StopFailuresRemaining = 1;
        fixture.Player.DisposeFailuresRemaining = 1;
        fixture.Windows.DisposeFailuresRemaining = 1;

        var error = await Assert.ThrowsAsync<VideoRendererControlException>(() => fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.ActivateAsync(token)), TestContext.Current.CancellationToken));

        Assert.Equal("activate", error.Operation);
        Assert.Equal("play failed", error.InnerException?.Message);
        Assert.Equal(1, fixture.Player.EndRemovalCount);
        Assert.Equal(1, fixture.Player.ErrorRemovalCount);
        Assert.Equal(1, fixture.Player.StopCount);
        Assert.Equal(1, fixture.Player.DisposeCount);
        Assert.Equal(1, fixture.Windows.DisposeCount);
        Assert.Equal(RendererLifecycle.Faulted, fixture.Renderer.Lifecycle);
    }

    [Fact]
    public async Task Dispose_WhenEveryCleanupStepThrows_AttemptsAllResourcesAndPublishesDisposed()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();
        fixture.Player.ThrowOnEndRemoval = true;
        fixture.Player.ThrowOnErrorRemoval = true;
        fixture.Player.StopFailuresRemaining = 1;
        fixture.Player.DisposeFailuresRemaining = 1;
        fixture.Windows.DisposeFailuresRemaining = 1;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () => await fixture.Renderer.DisposeAsync());

        Assert.Equal("end removal failed", error.Message);
        Assert.Equal(1, fixture.Player.EndRemovalCount);
        Assert.Equal(1, fixture.Player.ErrorRemovalCount);
        Assert.Equal(1, fixture.Player.StopCount);
        Assert.Equal(1, fixture.Player.DisposeCount);
        Assert.Equal(1, fixture.Windows.DisposeCount);
        Assert.Equal(RendererLifecycle.Disposed, fixture.Renderer.Lifecycle);
    }

    [Fact]
    public async Task NativeFault_WhenCleanupThrows_ObservesCallbackAndAttemptsEveryRelease()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();
        fixture.Player.ThrowOnEndRemoval = true;
        fixture.Player.ThrowOnErrorRemoval = true;
        fixture.Player.StopFailuresRemaining = 1;
        fixture.Player.DisposeFailuresRemaining = 1;
        fixture.Windows.DisposeFailuresRemaining = 1;

        fixture.Player.RaiseError("decoder.failed", "hardware decoder stopped");
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(RendererLifecycle.Faulted, fixture.Renderer.Lifecycle);
        Assert.Equal(1, fixture.Player.EndRemovalCount);
        Assert.Equal(1, fixture.Player.ErrorRemovalCount);
        Assert.Equal(1, fixture.Player.StopCount);
        Assert.Equal(1, fixture.Player.DisposeCount);
        Assert.Equal(1, fixture.Windows.DisposeCount);
    }

    [Fact]
    public async Task StopThenDispose_ReleasesMediaPlayerAndChildExactlyOnce()
    {
        await using var fixture = await RendererFixture.CreateActiveAsync();

        await fixture.Dispatcher.InvokeAsync(async token =>
        {
            await fixture.Renderer.StopAsync(token);
            await fixture.Renderer.StopAsync(token);
        }, TestContext.Current.CancellationToken);
        await fixture.Renderer.DisposeAsync();
        await fixture.Renderer.DisposeAsync();

        Assert.Equal(1, fixture.Player.StopCount);
        Assert.Equal(1, fixture.Player.DisposeCount);
        Assert.True(fixture.Player.CallbacksRemovedBeforeDispose);
        Assert.Equal(1, fixture.Windows.DisposeCount);
        Assert.Equal(RendererLifecycle.Disposed, fixture.Renderer.Lifecycle);
    }

    [Fact]
    public async Task StopFailure_RecordsCleanupDiagnosticWithoutLeakingTimerCallbacks()
    {
        var time = new ManualTimeProvider();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(time, diagnostics);
        fixture.Player.StopFailuresRemaining = 1;

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Dispatcher.InvokeAsync(
            token => new ValueTask(fixture.Renderer.StopAsync(token)),
            TestContext.Current.CancellationToken));

        var stop = Assert.Single(diagnostics.Events, static entry => entry.Operation == "stop" && entry.FailureCode is not null);
        Assert.Equal(nameof(InvalidOperationException), stop.FailureCode);

        time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Empty(diagnostics.Snapshots);
    }

    [Fact]
    public async Task MetricsTimer_PublishesSnapshotsAndStopsBeforeRendererDisposal()
    {
        var time = new ManualTimeProvider();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        await using var fixture = await RendererFixture.CreateActiveAsync(time, diagnostics);

        time.Advance(TimeSpan.FromSeconds(10));
        await fixture.Dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        var snapshot = Assert.Single(diagnostics.Snapshots);
        Assert.Equal(fixture.Renderer.Id, snapshot.RendererId);
        Assert.Equal("DISPLAY-VIDEO", snapshot.OutputKey);
        Assert.Equal("libvlc", snapshot.Backend);

        await fixture.Renderer.DisposeAsync();
        time.Advance(TimeSpan.FromSeconds(10));

        Assert.Single(diagnostics.Snapshots);
    }

    [Fact]
    public async Task CompositeFactory_RoutesKnownKindsAndThrowsTypedWebCapabilityError()
    {
        var solid = new RecordingRendererFactory();
        var video = new RecordingRendererFactory();
        var factory = new RendererFactory(solid, video);
        var solidDefinition = Definition(SolidColorSource.Create("#010203"));
        var videoDefinition = Definition(VideoSource.Create(VideoPath));
        var webDefinition = Definition(WebSource.Create(Path.GetTempPath(), "index.html"));

        Assert.Same(solid.Renderer, factory.Create(solidDefinition));
        Assert.Same(video.Renderer, factory.Create(videoDefinition));
        var error = Assert.Throws<RendererCapabilityUnavailableException>(() => factory.Create(webDefinition));

        Assert.Equal(WallpaperKind.Web, error.Kind);
        Assert.Equal(1, solid.CreateCount);
        Assert.Equal(1, video.CreateCount);
    }

    private static string VideoPath => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vibe-video-state.mp4"));

    private static WallpaperDefinition Definition(WallpaperSource source) => new(
        WallpaperId.New(), "Test", source, FitMode.Cover, 30, false,
        source is VideoSource, source is VideoSource ? 37 : 0, false);

    private static RendererContext Context(int volume = 0, NormalizedSourceRect? sourceCrop = null)
    {
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var identity = new MonitorIdentity("DISPLAY-VIDEO");
        var evidence = new MonitorIdentityEvidence(0, 0, 0, null, null, null, null, null, null, identity.Key, bounds);
        var monitor = new MonitorDescriptor(identity, evidence, identity.Key, bounds, bounds, 96, 1, DisplayOrientation.Landscape, true);
        return new RendererContext(
            hostHwnd: 500, monitor, bounds, bounds,
            new OutputWallpaperSettings(FitMode.Cover, 30, volume), sourceCrop);
    }

    private sealed class RendererFixture : IAsyncDisposable
    {
        private RendererFixture(
            EngineStaDispatcher dispatcher,
            VideoRenderer renderer,
            FakeVideoSurfaceWindowFactory windows,
            FakeLibVlcPlayer player,
            ManualTimeProvider? time)
        {
            Dispatcher = dispatcher;
            Renderer = renderer;
            Windows = windows;
            Player = player;
            Time = time;
        }

        public EngineStaDispatcher Dispatcher { get; }
        public VideoRenderer Renderer { get; }
        public FakeVideoSurfaceWindowFactory Windows { get; }
        public FakeLibVlcPlayer Player { get; }
        public ManualTimeProvider? Time { get; }

        public static async Task<RendererFixture> CreateReadyAsync(ManualTimeProvider? time = null)
        {
            var dispatcher = await EngineStaDispatcher.StartAsync();
            var runtime = new FakeLibVlcRuntime();
            var windows = new FakeVideoSurfaceWindowFactory();
            var renderer = new VideoRenderer(
                dispatcher,
                runtime,
                new FakeVideoProbeService(),
                windows,
                VideoRendererOptions.Default,
                diagnostics: null,
                timeProvider: time);
            await dispatcher.InvokeAsync(async token =>
            {
                await renderer.InitializeAsync(Context(25), token);
                await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
            }, TestContext.Current.CancellationToken);
            return new RendererFixture(dispatcher, renderer, windows, Assert.Single(runtime.Players), time);
        }

        public static async Task<RendererFixture> CreateActiveAsync(
            ManualTimeProvider? time = null,
            RecordingVideoPlaybackDiagnostics? diagnostics = null)
        {
            var dispatcher = await EngineStaDispatcher.StartAsync();
            var runtime = new FakeLibVlcRuntime();
            var windows = new FakeVideoSurfaceWindowFactory();
            var renderer = new VideoRenderer(
                dispatcher,
                runtime,
                new FakeVideoProbeService(),
                windows,
                VideoRendererOptions.Default,
                diagnostics: diagnostics,
                timeProvider: time);
            await dispatcher.InvokeAsync(async token =>
            {
                await renderer.InitializeAsync(Context(25), token);
                await renderer.LoadAsync(VideoSource.Create(VideoPath), token);
            }, TestContext.Current.CancellationToken);
            var fixture = new RendererFixture(dispatcher, renderer, windows, Assert.Single(runtime.Players), time);
            await fixture.Dispatcher.InvokeAsync(
                token => new ValueTask(fixture.Renderer.ActivateAsync(token)), TestContext.Current.CancellationToken);
            return fixture;
        }

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await Dispatcher.DisposeAsync();
        }
    }
}

internal sealed class RecordingVideoPlaybackDiagnostics : IVideoPlaybackDiagnostics
{
    public List<VideoPlaybackEvent> Events { get; } = [];
    public List<VideoPlaybackMetricsSnapshot> Snapshots { get; } = [];

    public void Record(VideoPlaybackEvent playbackEvent) => Events.Add(playbackEvent);

    public void Record(VideoPlaybackMetricsSnapshot snapshot) => Snapshots.Add(snapshot);
}

internal static class VideoRendererReflectionProbe
{
    private static readonly System.Reflection.MethodInfo FlushMetricsMethod =
        typeof(VideoRenderer).GetMethod("FlushMetrics", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("Expected private FlushMetrics method.");

    public static void InvokePrivateFlushMetrics(VideoRenderer renderer) =>
        FlushMetricsMethod.Invoke(renderer, null);
}

internal sealed class RecordingResumeObserver : IVideoResumeObserver
{
    public List<string> ResumedIds { get; } = [];
    public void NotifyResumed(string endpointId) => ResumedIds.Add(endpointId);
}

internal sealed class FakeLibVlcRuntime : ILibVlcRuntime
{
    public bool FailPlay { get; init; }
    public Action<FakeLibVlcPlayer>? NextPlayerConfiguration { get; set; }
    public List<FakeLibVlcPlayer> Players { get; } = [];
    public bool HardwareDecodingRequested => true;
    public string Version => "fake-3.0";

    public ILibVlcPlayer CreatePlayer()
    {
        var player = new FakeLibVlcPlayer { FailPlay = FailPlay };
        NextPlayerConfiguration?.Invoke(player);
        NextPlayerConfiguration = null;
        Players.Add(player);
        return player;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class FakeLibVlcPlayer : ILibVlcPlayer
{
    private EventHandler? _endReached;
    private EventHandler<VideoFaultEventArgs>? _encounteredError;
    private EventHandler<VideoPlaybackProgressEventArgs>? _playbackProgressed;

    public nint Hwnd { set => AssignedHwnd = value; }
    public nint AssignedHwnd { get; private set; }
    public long TimeMilliseconds { get; set; }
    public bool IsPlaying { get; private set; }
    public bool IsMuted { get; set; }
    public int VolumePercent { get; set; }
    public string? OpenedPath { get; private set; }
    public bool FailPlay { get; init; }
    public int PlayFailuresRemaining { get; set; }
    public int PauseFailuresRemaining { get; set; }
    public int OpenFailuresRemaining { get; set; }
    public int StopFailuresRemaining { get; set; }
    public int DisposeFailuresRemaining { get; set; }
    public bool ThrowOnEndRemoval { get; set; }
    public bool ThrowOnErrorRemoval { get; set; }
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }
    public int StopCount { get; private set; }
    public int DisposeCount { get; private set; }
    public bool CallbacksRemovedBeforeDispose { get; private set; }
    public int EndRemovalCount { get; private set; }
    public int ErrorRemovalCount { get; private set; }
    public (NormalizedSourceRect Crop, int Width, int Height)? AppliedSourceCrop { get; private set; }
    public List<string> Operations { get; } = [];

    public event EventHandler? EndReached
    {
        add => _endReached += value;
        remove
        {
            EndRemovalCount++;
            _endReached -= value;
            if (ThrowOnEndRemoval) throw new InvalidOperationException("end removal failed");
        }
    }

    public event EventHandler<VideoFaultEventArgs>? EncounteredError
    {
        add => _encounteredError += value;
        remove
        {
            ErrorRemovalCount++;
            _encounteredError -= value;
            if (ThrowOnErrorRemoval) throw new InvalidOperationException("error removal failed");
        }
    }

    public event EventHandler<VideoPlaybackProgressEventArgs>? PlaybackProgressed
    {
        add => _playbackProgressed += value;
        remove => _playbackProgressed -= value;
    }

    public void Open(string absolutePath, VideoMediaOpenOptions options)
    {
        Operations.Add("open");
        OpenedPath = absolutePath;
        if (OpenFailuresRemaining-- > 0) throw new InvalidOperationException("open failed");
    }

    public void Play()
    {
        Operations.Add("play");
        PlayCount++;
        if (FailPlay || PlayFailuresRemaining-- > 0) throw new InvalidOperationException("play failed");
        IsPlaying = true;
    }

    public void Pause()
    {
        PauseCount++;
        if (PauseFailuresRemaining-- > 0) throw new InvalidOperationException("pause failed");
        IsPlaying = false;
    }

    public void Stop()
    {
        StopCount++;
        if (StopFailuresRemaining-- > 0) throw new InvalidOperationException("stop failed");
        IsPlaying = false;
    }

    public void Dispose()
    {
        DisposeCount++;
        CallbacksRemovedBeforeDispose = _endReached is null && _encounteredError is null;
        if (DisposeFailuresRemaining-- > 0) throw new InvalidOperationException("player dispose failed");
    }

    public void RaiseEndReached() => _endReached?.Invoke(this, EventArgs.Empty);
    public void RaiseError(string code, string message) =>
        _encounteredError?.Invoke(this, new VideoFaultEventArgs(code, message));
    public void RaisePlaybackProgressed(long timeMilliseconds)
    {
        TimeMilliseconds = timeMilliseconds;
        _playbackProgressed?.Invoke(this, new VideoPlaybackProgressEventArgs(timeMilliseconds));
    }

    public void ApplySourceCrop(NormalizedSourceRect crop, int videoWidth, int videoHeight)
    {
        AppliedSourceCrop = (crop, videoWidth, videoHeight);
        Operations.Add("crop");
    }
}

internal sealed class ManualTimeProvider : TimeProvider
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
        foreach (var timer in _timers.ToArray())
        {
            timer.Advance(elapsed);
        }
    }

    private sealed class ManualTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private TimeSpan _remaining = dueTime;
        private TimeSpan _period = period;
        private bool _disposed;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (_disposed)
            {
                return false;
            }

            _remaining = dueTime;
            _period = period;
            return true;
        }

        public void Advance(TimeSpan elapsed)
        {
            if (_disposed || _remaining == Timeout.InfiniteTimeSpan)
            {
                return;
            }

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
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class FakeVideoSurfaceWindowFactory : IVideoSurfaceWindowFactory
{
    public nint CreatedHwnd { get; } = 701;
    public int DisposeCount { get; private set; }
    public int DisposeFailuresRemaining { get; set; }
    public bool IsVisible { get; private set; }

    public IVideoSurfaceWindow Create(nint parentHwnd) => new FakeWindow(this, CreatedHwnd);

    private sealed class FakeWindow(FakeVideoSurfaceWindowFactory owner, nint hwnd) : IVideoSurfaceWindow
    {
        private bool _disposed;
        public nint Hwnd { get; } = hwnd;
        public void Show() => owner.IsVisible = true;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            owner.DisposeCount++;
            if (owner.DisposeFailuresRemaining-- > 0) throw new InvalidOperationException("surface dispose failed");
        }
    }
}

internal sealed class FakeVideoProbeService : IVideoProbeService
{
    public Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken) =>
        Task.FromResult(new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(1), 30, "fake", true));
}

internal sealed class RecordingRendererFactory : IRendererFactory
{
    public IWallpaperRenderer Renderer { get; } = new RecordingRenderer();
    public int CreateCount { get; private set; }
    public IWallpaperRenderer Create(WallpaperDefinition definition)
    {
        CreateCount++;
        return Renderer;
    }

    private sealed class RecordingRenderer : IWallpaperRenderer
    {
        public RendererLifecycle Lifecycle => RendererLifecycle.Created;
        public PerformanceState PerformanceState => PerformanceState.Running;
        public RendererCapabilities Capabilities => RendererCapabilities.Web;
        public Task InitializeAsync(RendererContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task LoadAsync(WallpaperSource source, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ActivateAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyPerformanceAsync(RendererPerformanceRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
