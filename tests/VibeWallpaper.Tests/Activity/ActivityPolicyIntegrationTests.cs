using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Persistence;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Activity;

public sealed class ActivityPolicyIntegrationTests
{
    private static readonly MonitorIdentity OutputA = new("DISPLAY-A");
    private static readonly MonitorIdentity OutputB = new("DISPLAY-B");
    private static readonly DisplayViewport Canvas = new(0, 0, 3840, 1080);

    [Fact]
    public async Task ApplyActivitySnapshot_TwoOutputsStackReasonsAndOnlyTransitionOnEffectivePolicyChanges()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        await coordinator.ApplyAsync(Request("a", OutputA, 101, 0), TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request("b", OutputB, 202, 1920), TestContext.Current.CancellationToken);
        await using var engine = new WallpaperEngine(coordinator);
        var outputs = new[] { OutputA, OutputB };
        var baselineA = factory.Renderer("a").PerformanceCallCount;
        var baselineB = factory.Renderer("b").PerformanceCallCount;

        await engine.ApplyActivitySnapshotAsync(outputs, Snapshot(covered: OutputA), PerformancePolicyOptions.Default, null, new HashSet<PerformanceReason>(), TestContext.Current.CancellationToken);
        await engine.ApplyActivitySnapshotAsync(outputs, Snapshot(covered: OutputA, locked: true), PerformancePolicyOptions.Default, null, new HashSet<PerformanceReason>(), TestContext.Current.CancellationToken);
        await engine.ApplyActivitySnapshotAsync(outputs, Snapshot(locked: true), PerformancePolicyOptions.Default, null, new HashSet<PerformanceReason>(), TestContext.Current.CancellationToken);
        Assert.Equal(PerformanceState.Suspended, factory.Renderer("a").PerformanceState);
        Assert.Equal(PerformanceState.Suspended, factory.Renderer("b").PerformanceState);

        await engine.ApplyActivitySnapshotAsync(outputs, Snapshot(), PerformancePolicyOptions.Default, null, new HashSet<PerformanceReason>(), TestContext.Current.CancellationToken);
        await engine.ApplyActivitySnapshotAsync(outputs, Snapshot(), PerformancePolicyOptions.Default, null, new HashSet<PerformanceReason>(), TestContext.Current.CancellationToken);

        Assert.Equal(PerformanceState.Running, factory.Renderer("a").PerformanceState);
        Assert.Equal(PerformanceState.Running, factory.Renderer("b").PerformanceState);
        Assert.Equal(baselineA + 2, factory.Renderer("a").PerformanceCallCount);
        Assert.Equal(baselineB + 2, factory.Renderer("b").PerformanceCallCount);
    }

    [Fact]
    public async Task ApplyActivitySnapshot_DisabledConditionalOptionsDoNotSuppressSafetyOrSelectedPause()
    {
        var inner = new RecordingReasonEngine();
        await using var engine = new WallpaperEngine(inner);
        var disabled = new PerformancePolicyOptions(false, false, false, false, false, false, 30, 15, IncompatibleThrottleBehavior.Continue);

        await engine.ApplyActivitySnapshotAsync(
            [OutputA, OutputB],
            new ActivitySnapshot(true, true, true, false, false, true, [OutputA], [OutputB]),
            disabled,
            OutputB,
            new HashSet<PerformanceReason> { PerformanceReason.ExplorerUnavailable },
            TestContext.Current.CancellationToken);

        Assert.Equal([PerformanceReason.ExplorerUnavailable], inner.Reasons(OutputA, PerformanceReasonOwner.Activity));
        Assert.Equal([PerformanceReason.ExplorerUnavailable], inner.Reasons(OutputB, PerformanceReasonOwner.Activity));
        Assert.Empty(inner.Reasons(OutputA, PerformanceReasonOwner.User));
        Assert.Equal([PerformanceReason.UserPaused], inner.Reasons(OutputB, PerformanceReasonOwner.User));
    }

    [Fact]
    public async Task PauseAllAndLaterActivitySnapshot_ShareSerializationAndKeepEveryOutputPaused()
    {
        var inner = new RecordingReasonEngine { BlockFirstPause = true };
        await using var engine = new WallpaperEngine(inner);
        var pause = engine.SetPausedAllAsync(
            [OutputA, OutputB],
            true,
            TestContext.Current.CancellationToken);
        await inner.FirstPauseStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var snapshot = engine.ApplyActivitySnapshotAsync(
            [OutputA, OutputB],
            Snapshot(),
            PerformancePolicyOptions.Default,
            null,
            new HashSet<PerformanceReason>(),
            TestContext.Current.CancellationToken);
        await Task.Yield();
        Assert.False(snapshot.IsCompleted);
        inner.ReleaseFirstPause.TrySetResult();
        await Task.WhenAll(pause, snapshot);

        Assert.Equal([PerformanceReason.UserPaused], inner.Reasons(OutputA, PerformanceReasonOwner.User));
        Assert.Equal([PerformanceReason.UserPaused], inner.Reasons(OutputB, PerformanceReasonOwner.User));
        Assert.DoesNotContain(inner.UserReasonWrites, write => write.Reasons.Count == 0);
    }

    private static ActivitySnapshot Snapshot(MonitorIdentity? covered = null, bool locked = false) =>
        new(locked, false, false, false, false, false, covered is null ? [] : [covered], []);

    private static AssignmentRequest Request(string name, MonitorIdentity output, nint hwnd, int x)
    {
        var definition = new WallpaperDefinition(
            new WallpaperId(Guid.NewGuid()), name, SolidColorSource.Create("#123456"),
            FitMode.Cover, 30, false, false, 0, false);
        return new AssignmentRequest(
            definition,
            DisplayMode.Independent,
            null,
            Canvas,
            [new OutputAssignmentTarget(output, hwnd, new DisplayViewport(x, 0, 1920, 1080), new OutputWallpaperSettings(FitMode.Cover, 30, 0))]);
    }

    private sealed class RecordingReasonEngine : IWallpaperEngine
    {
        private readonly Dictionary<(string, PerformanceReasonOwner), HashSet<PerformanceReason>> _reasons = [];
        public bool BlockFirstPause { get; init; }
        public TaskCompletionSource FirstPauseStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstPause { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<(string Output, IReadOnlySet<PerformanceReason> Reasons)> UserReasonWrites { get; } = [];

        public IReadOnlyList<PerformanceReason> Reasons(MonitorIdentity output, PerformanceReasonOwner owner) =>
            _reasons.GetValueOrDefault((output.Key, owner), []).Order().ToArray();

        public async Task SetReasonsAsync(MonitorIdentity output, PerformanceReasonOwner owner, IReadOnlySet<PerformanceReason> reasons, CancellationToken cancellationToken)
        {
            if (owner == PerformanceReasonOwner.User)
            {
                UserReasonWrites.Add((output.Key, new HashSet<PerformanceReason>(reasons)));
                if (BlockFirstPause && reasons.Contains(PerformanceReason.UserPaused) && !FirstPauseStarted.Task.IsCompleted)
                {
                    FirstPauseStarted.TrySetResult();
                    await ReleaseFirstPause.Task.WaitAsync(cancellationToken);
                }
            }
            _reasons[(output.Key, owner)] = [.. reasons];
        }

        public Task<AssignmentResult> ApplyAsync(AssignmentRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public EngineSnapshot GetSnapshot() => new(PersistedState.Default, []);
    }
}
