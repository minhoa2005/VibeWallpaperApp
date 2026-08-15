using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class WallpaperEngineTests
{
    [Fact]
    public async Task InitializeFallbacksAsync_ProjectsRuntimeFallbackIntoEngineSnapshot()
    {
        var output = new MonitorIdentity("DISPLAY-A");
        var assigned = WallpaperId.New();
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var assignment = new WallpaperAssignment(
            new PersistedMonitorReference(output, new MonitorIdentityEvidence(1, 1, 1, null, null, null, null, null, null, "Display", bounds)),
            assigned, DisplayMode.Independent, FitMode.Cover, 30, 0, null);
        var state = new PersistedState(1, [], [assignment], [], null);
        var runtime = new RecordingEngine
        {
            Snapshot = new EngineSnapshot(state, [new OutputRuntimeSnapshot(output, 0, null, null)]),
        };
        var fallback = new FallbackRendererCoordinator(state, AppSettings.Default, new NoopRuntimeActivator());
        await using var engine = new WallpaperEngine(runtime, fallback);

        var result = await engine.InitializeFallbacksAsync(_ => false, TestContext.Current.CancellationToken);

        Assert.Empty(result.Diagnostics);
        var effective = Assert.Single(engine.GetSnapshot().Outputs).EffectiveState;
        Assert.NotNull(effective);
        Assert.Equal(assigned, effective.AssignedWallpaper);
        Assert.Equal(EffectiveWallpaperKind.SolidFallback, effective.EffectiveKind);
    }

    [Fact]
    public async Task SetPausedAllAsync_WhenOneOutputFails_RollsBackEarlierOutputsAndReturnsTypedFailure()
    {
        var first = new MonitorIdentity("DISPLAY-A");
        var second = new MonitorIdentity("DISPLAY-B");
        var runtime = new RecordingEngine { FailingOutput = second };
        await using var engine = new WallpaperEngine(runtime);

        var result = await engine.SetPausedAllAsync([first, second], paused: true, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal("wallpaper.pause.failed", result.ErrorCode);
        Assert.False(engine.IsPaused);
        Assert.Equal(
            [(first.Key, true), (second.Key, true), (first.Key, false)],
            runtime.PauseTransitions);
    }

    private sealed class RecordingEngine : IWallpaperEngine
    {
        public MonitorIdentity? FailingOutput { get; init; }
        public List<(string Output, bool Paused)> PauseTransitions { get; } = [];
        public EngineSnapshot Snapshot { get; init; } = new(PersistedState.Default, []);
        public Task<AssignmentResult> ApplyAsync(AssignmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public EngineSnapshot GetSnapshot() => Snapshot;

        public Task SetReasonsAsync(MonitorIdentity output, PerformanceReasonOwner owner, IReadOnlySet<PerformanceReason> reasons, CancellationToken cancellationToken)
        {
            var paused = reasons.Contains(PerformanceReason.UserPaused);
            PauseTransitions.Add((output.Key, paused));
            return output == FailingOutput && paused
                ? Task.FromException(new InvalidOperationException("injected pause failure"))
                : Task.CompletedTask;
        }
    }

    private sealed class NoopRuntimeActivator : IRuntimeWallpaperActivator
    {
        public Task ActivateAsync(
            MonitorIdentity output,
            WallpaperDefinition wallpaper,
            WallpaperAssignment persistedAssignment,
            long generation,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
