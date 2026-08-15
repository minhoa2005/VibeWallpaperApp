using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class FallbackRendererCoordinatorTests
{
    private static readonly MonitorIdentity FirstOutput = new("DISPLAY-A");
    private static readonly MonitorIdentity SecondOutput = new("DISPLAY-B");

    [Theory]
    [InlineData(SourceValidationStatus.Missing, "wallpaper.source.missing")]
    [InlineData(SourceValidationStatus.Invalid, "wallpaper.source.invalid")]
    [InlineData(SourceValidationStatus.Unsupported, "wallpaper.source.unsupported")]
    public async Task InitializeAsync_WhenPersistedSourceCannotRender_ActivatesSolidFallbackWithoutChangingState(
        SourceValidationStatus sourceStatus,
        string reasonCode)
    {
        var state = State(sourceStatus, includeSecondOutput: false);
        var originalAssignment = state.Assignments.Single();
        var activator = new RecordingRuntimeActivator();
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);

        await coordinator.InitializeAsync(_ => true, TestContext.Current.CancellationToken);

        var effective = coordinator.GetEffectiveState(FirstOutput);
        Assert.Equal(originalAssignment.Wallpaper, effective.AssignedWallpaper);
        Assert.Equal(EffectiveWallpaperKind.SolidFallback, effective.EffectiveKind);
        Assert.Equal(reasonCode, effective.FallbackReasonCode);
        Assert.IsType<SolidColorSource>(activator.Activations.Single().Wallpaper.Source);
        Assert.Same(originalAssignment, state.Assignments.Single());
        Assert.Equal(originalAssignment.GroupId, state.Assignments.Single().GroupId);
        Assert.Equal(originalAssignment.TargetFps, state.Assignments.Single().TargetFps);
        Assert.Equal(1, coordinator.GetGeneration(FirstOutput));
    }

    [Fact]
    public async Task ReconcileAsync_WhenSourceDisappearsThenReturns_UsesNewGenerationsAndRestoresOriginal()
    {
        var state = State(SourceValidationStatus.Available, includeSecondOutput: false);
        var assigned = state.Assignments.Single().Wallpaper;
        var activator = new RecordingRuntimeActivator();
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);
        await coordinator.InitializeAsync(_ => true, TestContext.Current.CancellationToken);

        await coordinator.ReconcileAsync(
            FirstOutput,
            SourceValidationStatus.Missing,
            rendererAvailable: true,
            TestContext.Current.CancellationToken);
        await coordinator.ReconcileAsync(
            FirstOutput,
            SourceValidationStatus.Available,
            rendererAvailable: true,
            TestContext.Current.CancellationToken);

        Assert.Equal([1L, 2L, 3L], activator.Activations.Select(static item => item.Generation));
        var effective = coordinator.GetEffectiveState(FirstOutput);
        Assert.Equal(EffectiveWallpaperKind.Assigned, effective.EffectiveKind);
        Assert.Equal(assigned, effective.AssignedWallpaper);
        Assert.Equal(assigned, effective.EffectiveWallpaper);
        Assert.Null(effective.FallbackReasonCode);
        Assert.Equal(assigned, state.Assignments.Single().Wallpaper);
    }

    [Fact]
    public async Task ReconcileAsync_WhenRendererCapabilityIsUnavailable_OnlyAffectedOutputFallsBack()
    {
        var state = State(SourceValidationStatus.Available, includeSecondOutput: true);
        var activator = new RecordingRuntimeActivator();
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);
        await coordinator.InitializeAsync(_ => true, TestContext.Current.CancellationToken);

        await coordinator.ReconcileAsync(
            FirstOutput,
            SourceValidationStatus.Available,
            rendererAvailable: false,
            TestContext.Current.CancellationToken);

        Assert.Equal(EffectiveWallpaperKind.SolidFallback, coordinator.GetEffectiveState(FirstOutput).EffectiveKind);
        Assert.Equal("wallpaper.renderer.unavailable", coordinator.GetEffectiveState(FirstOutput).FallbackReasonCode);
        Assert.Equal(EffectiveWallpaperKind.Assigned, coordinator.GetEffectiveState(SecondOutput).EffectiveKind);
        Assert.Equal(2, coordinator.GetGeneration(FirstOutput));
        Assert.Equal(1, coordinator.GetGeneration(SecondOutput));
        Assert.Equal(2, state.Assignments.Count);
    }

    [Fact]
    public async Task ReconcileAsync_WhenFallbackActivationFails_PreservesPriorEffectiveAndPersistedStates()
    {
        var state = State(SourceValidationStatus.Available, includeSecondOutput: false);
        var activator = new RecordingRuntimeActivator();
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);
        await coordinator.InitializeAsync(_ => true, TestContext.Current.CancellationToken);
        var before = coordinator.GetEffectiveState(FirstOutput);
        activator.FailSolid = true;

        await Assert.ThrowsAsync<FallbackActivationException>(() => coordinator.ReconcileAsync(
            FirstOutput,
            SourceValidationStatus.Missing,
            rendererAvailable: true,
            TestContext.Current.CancellationToken));

        Assert.Equal(before, coordinator.GetEffectiveState(FirstOutput));
        Assert.Equal(before.AssignedWallpaper, state.Assignments.Single().Wallpaper);
        Assert.Equal(2, coordinator.GetGeneration(FirstOutput));
    }

    [Fact]
    public async Task InitializeAsync_WhenOneFallbackActivationFails_ContinuesOtherOutputsAndReturnsTypedDiagnostic()
    {
        var state = State(SourceValidationStatus.Missing, includeSecondOutput: true);
        var activator = new RecordingRuntimeActivator { FailSolidOutputKey = FirstOutput.Key };
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);

        var result = await coordinator.InitializeAsync(_ => false, TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(FirstOutput, diagnostic.Output);
        Assert.Equal("wallpaper.fallback.activation_failed", diagnostic.Code);
        Assert.Equal(EffectiveWallpaperKind.SolidFallback, coordinator.GetEffectiveState(SecondOutput).EffectiveKind);
        Assert.Contains(activator.Activations, activation => activation.Output == SecondOutput);
    }

    [Fact]
    public async Task InitializeAsync_WhenPersistedOutputIsDisconnected_SkipsItAndContinuesOtherOutputs()
    {
        var state = State(SourceValidationStatus.Available, includeSecondOutput: true);
        var failure = new ArgumentException("Output is not connected.", "selectedOutputs");
        var activator = new RecordingRuntimeActivator
        {
            FailureOutputKey = FirstOutput.Key,
            Failure = failure,
        };
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);

        var result = await coordinator.InitializeAsync(_ => true, TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(FirstOutput, diagnostic.Output);
        Assert.Equal("wallpaper.restore.skipped", diagnostic.Code);
        Assert.Equal("wallpaper.output.disconnected", diagnostic.ReasonCode);
        Assert.Equal("Không thể khôi phục wallpaper trên một màn hình đã ngắt kết nối. Ứng dụng vẫn tiếp tục chạy.", diagnostic.Message);
        Assert.Same(failure, diagnostic.Exception);
        Assert.Contains(activator.Activations, activation => activation.Output == SecondOutput);
    }

    [Fact]
    public async Task InitializeAsync_WhenUnexpectedActivationFails_ReturnsSafeDiagnosticAndContinues()
    {
        var state = State(SourceValidationStatus.Available, includeSecondOutput: true);
        var failure = new IOException(@"Cannot read C:\private\wallpaper.mp4");
        var activator = new RecordingRuntimeActivator
        {
            FailureOutputKey = FirstOutput.Key,
            Failure = failure,
        };
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);

        var result = await coordinator.InitializeAsync(_ => true, TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal("wallpaper.restore.failed", diagnostic.Code);
        Assert.Equal("wallpaper.restore.unexpected", diagnostic.ReasonCode);
        Assert.DoesNotContain("private", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Same(failure, diagnostic.Exception);
        Assert.Contains(activator.Activations, activation => activation.Output == SecondOutput);
    }

    [Fact]
    public async Task InitializeAsync_WhenCallerCancels_PropagatesCancellation()
    {
        var state = State(SourceValidationStatus.Available, includeSecondOutput: false);
        var activator = new RecordingRuntimeActivator();
        var coordinator = new FallbackRendererCoordinator(state, AppSettings.Default, activator);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            coordinator.InitializeAsync(_ => true, cancellation.Token));
    }

    private static PersistedState State(SourceValidationStatus status, bool includeSecondOutput)
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(),
            "Persisted video",
            VideoSource.Create(Path.Combine(Path.GetTempPath(), "wallpaper.mp4")),
            FitMode.Contain,
            24,
            false,
            true,
            37,
            false);
        var group = DisplayGroupId.New();
        var assignments = new List<WallpaperAssignment>
        {
            Assignment(FirstOutput, definition.Id, group),
        };
        if (includeSecondOutput)
        {
            assignments.Add(Assignment(SecondOutput, definition.Id, group));
        }

        return new PersistedState(
            1,
            [new WallpaperLibraryItem(
                definition,
                null,
                null,
                new SourceValidation(status, null, status == SourceValidationStatus.Available ? null : $"source.{status.ToString().ToLowerInvariant()}", DateTimeOffset.UtcNow))],
            assignments,
            [new PersistedDisplayGroup(group, DisplayMode.Duplicate, definition.Id, assignments.Select(static item => item.Monitor.Identity).ToArray())],
            null);
    }

    private static WallpaperAssignment Assignment(MonitorIdentity output, WallpaperId wallpaper, DisplayGroupId group)
    {
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var evidence = new MonitorIdentityEvidence(1, 1, 1, null, null, null, null, null, null, output.Key, bounds);
        return new WallpaperAssignment(
            new PersistedMonitorReference(output, evidence),
            wallpaper,
            DisplayMode.Duplicate,
            FitMode.Contain,
            24,
            37,
            group);
    }

    private sealed class RecordingRuntimeActivator : IRuntimeWallpaperActivator
    {
        public List<RuntimeActivation> Activations { get; } = [];
        public bool FailSolid { get; set; }
        public string? FailSolidOutputKey { get; init; }
        public string? FailureOutputKey { get; init; }
        public Exception? Failure { get; init; }

        public Task ActivateAsync(
            MonitorIdentity output,
            WallpaperDefinition wallpaper,
            WallpaperAssignment persistedAssignment,
            long generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (output.Key == FailureOutputKey && Failure is not null)
            {
                throw Failure;
            }

            if ((FailSolid || output.Key == FailSolidOutputKey) && wallpaper.Source is SolidColorSource)
            {
                throw new InvalidOperationException("injected solid fallback failure");
            }

            Activations.Add(new RuntimeActivation(output, wallpaper, persistedAssignment, generation));
            return Task.CompletedTask;
        }
    }

    private sealed record RuntimeActivation(
        MonitorIdentity Output,
        WallpaperDefinition Wallpaper,
        WallpaperAssignment Assignment,
        long Generation);
}
