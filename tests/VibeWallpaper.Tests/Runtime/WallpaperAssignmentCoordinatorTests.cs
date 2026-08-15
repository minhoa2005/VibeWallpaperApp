using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Engine.Persistence;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Runtime;

public sealed class WallpaperAssignmentCoordinatorTests
{
    private static readonly MonitorIdentity FirstOutput = new("DISPLAY-A");
    private static readonly MonitorIdentity SecondOutput = new("DISPLAY-B");
    private static readonly DisplayViewport Canvas = new(0, 0, 3840, 1080);

    [Fact]
    public async Task ApplyAsync_WhenOlderPrepareFinishesLast_OnlyNewestGenerationCommits()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var first = new RendererBarrier(observeCancellation: false);
        factory.Plan("first").LoadBarrier = first;
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);

        var oldApply = coordinator.ApplyAsync(Request("first", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await first.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var newResult = await coordinator.ApplyAsync(Request("second", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        first.Release();
        var oldResult = await oldApply;

        Assert.Equal(AssignmentOutcome.Applied, newResult.Outcome);
        Assert.Equal(AssignmentOutcome.Superseded, oldResult.Outcome);
        Assert.Equal("second", factory.Active(101)!.Name);
        Assert.Equal(Definition("second").Id, state.State.Assignments.Single().Wallpaper);
        Assert.Equal(1, factory.Renderer("first").DisposeCount);
    }

    [Fact]
    public async Task ApplyAsync_WhenCandidateActivationFails_OldRendererAndPersistedStateRemain()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("stable", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        factory.Plan("broken").ActivationFailure = new InvalidOperationException("injected activation failure");

        await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(Request("broken", Target(FirstOutput, 101)), TestContext.Current.CancellationToken));

        Assert.Equal("stable", factory.Active(101)!.Name);
        Assert.Equal(Definition("stable").Id, state.State.Assignments.Single().Wallpaper);
        Assert.Equal(1, factory.Renderer("broken").DisposeCount);
    }

    [Fact]
    public async Task ApplyAsync_WhenCallerCancelsDuringPrepare_DisposesCandidateOnceAndPreservesState()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("stable", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        var blocked = new RendererBarrier();
        factory.Plan("cancelled").LoadBarrier = blocked;
        using var cancellation = new CancellationTokenSource();
        var apply = coordinator.ApplyAsync(Request("cancelled", Target(FirstOutput, 101)), cancellation.Token);
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);

        Assert.Equal("stable", factory.Active(101)!.Name);
        Assert.Equal(Definition("stable").Id, state.State.Assignments.Single().Wallpaper);
        Assert.Equal(1, factory.Renderer("cancelled").DisposeCount);
    }

    [Fact]
    public async Task ApplyAsync_TwoRequestsForSameOutput_SerializeCommitAndNewestWins()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var activation = new RendererBarrier(observeCancellation: false);
        factory.Plan("first").ActivationBarrier = activation;
        var secondLoad = new RendererBarrier();
        secondLoad.Release();
        factory.Plan("second").LoadBarrier = secondLoad;
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        var firstApply = coordinator.ApplyAsync(Request("first", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await activation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var secondApply = coordinator.ApplyAsync(Request("second", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await secondLoad.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        activation.Release();
        var results = await Task.WhenAll(firstApply, secondApply);

        Assert.Contains(results, result => result.Outcome == AssignmentOutcome.Superseded);
        Assert.Contains(results, result => result.Outcome == AssignmentOutcome.Applied);
        Assert.Equal("second", factory.Active(101)!.Name);
        Assert.Equal(1, state.SaveCount);
    }

    [Fact]
    public async Task ApplyAsync_DifferentOutputs_DoNotWaitForAnotherOutputsPreparation()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var blocked = new RendererBarrier(observeCancellation: false);
        factory.Plan("slow").LoadBarrier = blocked;
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        var slowApply = coordinator.ApplyAsync(Request("slow", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var fastResult = await coordinator.ApplyAsync(Request("fast", Target(SecondOutput, 202)), TestContext.Current.CancellationToken);
        blocked.Release();
        await slowApply;

        Assert.Equal(AssignmentOutcome.Applied, fastResult.Outcome);
        Assert.Equal("fast", factory.Active(202)!.Name);
    }

    [Fact]
    public async Task SetReasonsAsync_DuringPrepare_ReentersDispatcherAndSuspendsBeforeActivation()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var blocked = new RendererBarrier(observeCancellation: false);
        factory.Plan("slow").LoadBarrier = blocked;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(Request("slow", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.Activity,
            new HashSet<PerformanceReason> { PerformanceReason.SessionLocked },
            TestContext.Current.CancellationToken);
        blocked.Release();
        await apply;

        var renderer = factory.Renderer("slow");
        Assert.Equal(PerformanceState.Suspended, renderer.PerformanceState);
        Assert.False(renderer.RenderedRunningFrame);
    }

    [Fact]
    public async Task ApplyAsync_WhenStateSaveFails_ReactivatesOldRendererAndPreservesLogicalState()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("stable", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        state.NextSaveFailure = new IOException("injected save failure");

        await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(Request("candidate", Target(FirstOutput, 101)), TestContext.Current.CancellationToken));

        Assert.Equal("stable", factory.Active(101)!.Name);
        Assert.Equal(Definition("stable").Id, state.State.Assignments.Single().Wallpaper);
        Assert.Equal(1, factory.Renderer("candidate").DisposeCount);
    }

    [Fact]
    public async Task ApplyAsync_GroupSwapFailsOnLaterTarget_RollsBackEarlierTargetInReverse()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("old-a", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request("old-b", Target(SecondOutput, 202)), TestContext.Current.CancellationToken);
        factory.Plan("group").ActivationFailure = new InvalidOperationException("swap failed");
        factory.Plan("group").ActivationFailureHost = 202;

        await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken));

        Assert.Equal("old-a", factory.Active(101)!.Name);
        Assert.Equal("old-b", factory.Active(202)!.Name);
        Assert.All(factory.Renderers("group"), static renderer => Assert.Equal(1, renderer.DisposeCount));
        Assert.DoesNotContain(state.State.Groups, group => group.Wallpaper == Definition("group").Id);
        Assert.Equal(1, factory.Renderer("group").DisposeCount);
    }

    [Fact]
    public async Task ApplyAsync_GroupPrepareFailsBeforeGates_NoTargetIsSwapped()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("old-a", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request("old-b", Target(SecondOutput, 202)), TestContext.Current.CancellationToken);
        factory.Plan("group").LoadFailure = new IOException("prepare failed");
        factory.Plan("group").LoadFailureHost = 101;

        await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken));

        Assert.Equal("old-a", factory.Active(101)!.Name);
        Assert.Equal("old-b", factory.Active(202)!.Name);
    }

    [Fact]
    public async Task ApplyAsync_GroupPrepareFailure_CancelsSiblingPreparation()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var sibling = new RendererBarrier();
        factory.Plan("group").LoadBarrier = sibling;
        factory.Plan("group").LoadBarrierHost = 202;
        factory.Plan("group").LoadFailure = new IOException("target one prepare failed");
        factory.Plan("group").LoadFailureHost = 101;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());

        await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken));

        Assert.False(sibling.Started.Task.IsCompleted);
        Assert.All(factory.Renderers("group"), static renderer => Assert.Equal(1, renderer.DisposeCount));
    }

    [Fact]
    public async Task ApplyAsync_MonitorDisconnectDuringLoad_ReturnsHostDiagnosticWithoutActivation()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var blocked = new RendererBarrier(observeCancellation: false);
        factory.Plan("candidate").LoadBarrier = blocked;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(Request("candidate", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.Topology,
            new HashSet<PerformanceReason> { PerformanceReason.MonitorDisconnected },
            TestContext.Current.CancellationToken);
        blocked.Release();

        var result = await apply;

        Assert.Equal(AssignmentOutcome.Superseded, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == AssignmentDiagnosticCode.HostUnavailable);
        Assert.Null(factory.Active(101));
        Assert.Equal(1, factory.Renderer("candidate").DisposeCount);
    }

    [Fact]
    public async Task ApplyAsync_GroupRollbackFailure_IsReportedAsDiagnostic()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        await coordinator.ApplyAsync(Request("old-a", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request("old-b", Target(SecondOutput, 202)), TestContext.Current.CancellationToken);
        factory.Plan("old-a").ActivationFailure = new InvalidOperationException("rollback failed");
        factory.Plan("group").ActivationFailure = new InvalidOperationException("swap failed");
        factory.Plan("group").ActivationFailureHost = 202;

        var failure = await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken));

        Assert.Contains(failure.Diagnostics, diagnostic =>
            diagnostic.Output == FirstOutput && diagnostic.Code == AssignmentDiagnosticCode.RollbackFailed);
    }

    [Fact]
    public async Task ApplyAsync_GroupStateSaveFails_RollsBackEverySwappedOutputAndPreservesGroupState()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("old-a", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request("old-b", Target(SecondOutput, 202)), TestContext.Current.CancellationToken);
        var oldAssignments = state.State.Assignments.Select(static assignment => assignment.Wallpaper).ToArray();
        state.NextSaveFailure = new IOException("group save failed");

        await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken));

        Assert.Equal("old-a", factory.Active(101)!.Name);
        Assert.Equal("old-b", factory.Active(202)!.Name);
        Assert.Equal(oldAssignments, state.State.Assignments.Select(static assignment => assignment.Wallpaper));
        Assert.Empty(state.State.Groups);
        Assert.All(factory.Renderers("group"), static renderer => Assert.Equal(1, renderer.DisposeCount));
    }

    [Fact]
    public async Task SetReasonsAsync_SessionLockArrivesDuringSwap_CandidateActivatesSuspended()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var activation = new RendererBarrier(observeCancellation: false);
        factory.Plan("candidate").ActivationBarrier = activation;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(Request("candidate", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await activation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var renderer = factory.Renderer("candidate");

        var policyUpdate = coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.Activity,
            new HashSet<PerformanceReason> { PerformanceReason.SessionLocked },
            TestContext.Current.CancellationToken);
        await renderer.SuspendedSet.Task.WaitAsync(TestContext.Current.CancellationToken);
        activation.Release();
        await Task.WhenAll(apply, policyUpdate);

        Assert.Equal(PerformanceState.Suspended, renderer.PerformanceState);
        Assert.False(renderer.RenderedRunningFrame);
        Assert.Same(renderer, factory.Active(101));
    }

    [Fact]
    public async Task SetReasonsAsync_FullscreenArrivesDuringLoad_CandidateNeverActivatesRunning()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var blocked = new RendererBarrier(observeCancellation: false);
        factory.Plan("candidate").LoadBarrier = blocked;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(Request("candidate", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        await coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.Activity,
            new HashSet<PerformanceReason> { PerformanceReason.FullscreenCovered },
            TestContext.Current.CancellationToken);
        blocked.Release();
        await apply;

        var renderer = factory.Renderer("candidate");
        Assert.Equal(PerformanceState.Suspended, renderer.PerformanceState);
        Assert.False(renderer.RenderedRunningFrame);
    }

    [Fact]
    public async Task SetReasonsAsync_BatteryReasonArrivesDuringSwap_CandidateActivatesThrottled()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var activation = new RendererBarrier(observeCancellation: false);
        factory.Plan("candidate").ActivationBarrier = activation;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(Request("candidate", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await activation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var renderer = factory.Renderer("candidate");

        var policyUpdate = coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.Activity,
            new HashSet<PerformanceReason> { PerformanceReason.Battery },
            TestContext.Current.CancellationToken);
        await renderer.ThrottledSet.Task.WaitAsync(TestContext.Current.CancellationToken);
        activation.Release();
        await Task.WhenAll(apply, policyUpdate);

        Assert.Equal(PerformanceState.Throttled, renderer.PerformanceState);
        Assert.Same(renderer, factory.Active(101));
    }

    [Fact]
    public async Task SetReasonsAsync_ShutdownDuringLoad_CancelsTransitionAndDisposesCandidateOnce()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var blocked = new RendererBarrier(observeCancellation: false);
        factory.Plan("candidate").LoadBarrier = blocked;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(Request("candidate", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await blocked.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.Shutdown,
            new HashSet<PerformanceReason> { PerformanceReason.Shutdown },
            TestContext.Current.CancellationToken);
        blocked.Release();

        var result = await apply;

        Assert.Equal(AssignmentOutcome.Superseded, result.Outcome);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == AssignmentDiagnosticCode.HostUnavailable);
        Assert.Equal(1, factory.Renderer("candidate").DisposeCount);
        Assert.Null(factory.Active(101));
    }

    [Fact]
    public async Task ApplyAsync_WhenSaveFinishesAfterNewerPrepareFails_RestoresPreviousPersistedState()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("stable", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        state.SaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.SaveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.SaveObservesCancellation = false;
        var older = coordinator.ApplyAsync(Request("older", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await state.SaveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        factory.Plan("newer").LoadFailure = new IOException("newer prepare failed");

        await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(Request("newer", Target(FirstOutput, 101)), TestContext.Current.CancellationToken));
        state.SaveRelease.TrySetResult();
        var olderResult = await older;

        Assert.Equal(AssignmentOutcome.Superseded, olderResult.Outcome);
        Assert.Equal(Definition("stable").Id, state.State.Assignments.Single().Wallpaper);
        Assert.Equal("stable", factory.Active(101)!.Name);
    }

    [Fact]
    public async Task ApplyAsync_WhenGenerationChangesDuringCommitPolicyAwait_StaleCandidateNeverActivates()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var policy = new RendererBarrier(observeCancellation: false);
        factory.Plan("older").PerformanceBarrier = policy;
        factory.Plan("older").PerformanceBarrierOnCall = 2;
        var newerLoad = new RendererBarrier();
        newerLoad.Release();
        factory.Plan("newer").LoadBarrier = newerLoad;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var older = coordinator.ApplyAsync(Request("older", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await policy.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var newer = coordinator.ApplyAsync(Request("newer", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        await newerLoad.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        policy.Release();
        var results = await Task.WhenAll(older, newer);

        Assert.Equal(AssignmentOutcome.Superseded, results[0].Outcome);
        Assert.Equal(0, factory.Renderer("older").ActivateCount);
        Assert.Equal(1, factory.Renderer("older").DisposeCount);
        Assert.Equal("newer", factory.Active(101)!.Name);
    }

    [Fact]
    public async Task ApplyAsync_WhenShutdownArrivesDuringGroupCommitPolicyAwait_NoGroupCandidateActivates()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var policy = new RendererBarrier(observeCancellation: false);
        factory.Plan("group").PerformanceBarrier = policy;
        factory.Plan("group").PerformanceBarrierOnCall = 2;
        factory.Plan("group").PerformanceBarrierHost = 101;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken);
        await policy.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var shutdown = coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.Shutdown,
            new HashSet<PerformanceReason> { PerformanceReason.Shutdown },
            TestContext.Current.CancellationToken);
        await factory.Renderers("group")[0].SuspendedSet.Task.WaitAsync(TestContext.Current.CancellationToken);
        policy.Release();
        var result = await apply;
        await shutdown;

        Assert.Equal(AssignmentOutcome.Superseded, result.Outcome);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == AssignmentDiagnosticCode.HostUnavailable);
        Assert.All(factory.Renderers("group"), static renderer => Assert.Equal(0, renderer.ActivateCount));
    }

    [Fact]
    public void AssignmentCommit_WhenOneMemberLeavesExistingGroup_PreservesSiblingAsIndependentAndClearsAudioOwner()
    {
        var groupId = new DisplayGroupId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));
        var video = VideoDefinition("video");
        var solid = Definition("solid");
        var initial = ValidState(
            [video, solid],
            [PersistedAssignment(FirstOutput, video, DisplayMode.Duplicate, groupId),
             PersistedAssignment(SecondOutput, video, DisplayMode.Duplicate, groupId)],
            [new PersistedDisplayGroup(groupId, DisplayMode.Duplicate, video.Id, [FirstOutput, SecondOutput])],
            SecondOutput);

        var commit = AssignmentCommit.Create(initial, Request("solid", Target(FirstOutput, 101)));
        var validated = PersistedStateValidator.ValidateAndNormalize(commit.Next);

        Assert.Equal(2, validated.Assignments.Count);
        var sibling = Assert.Single(validated.Assignments, assignment => assignment.Monitor.Identity == SecondOutput);
        Assert.Equal(DisplayMode.Independent, sibling.Mode);
        Assert.Null(sibling.GroupId);
        Assert.Equal(video.Id, sibling.Wallpaper);
        Assert.Empty(validated.Groups);
        Assert.Null(validated.AudioOwner);
    }

    [Fact]
    public async Task ApplyRuntimeOnlyAsync_ActivatesWithoutSavingOrChangingLogicalSnapshot()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, new FakeWallpaperRendererFactory(), state);

        var result = await coordinator.ApplyRuntimeOnlyAsync(
            Request("runtime-only", Target(FirstOutput, 101)),
            TestContext.Current.CancellationToken);

        Assert.Equal(AssignmentOutcome.Applied, result.Outcome);
        Assert.False(result.Persisted);
        Assert.Equal(0, state.SaveCount);
        Assert.Empty(coordinator.GetSnapshot().State.Assignments);
    }

    [Fact]
    public async Task ApplyAsync_WhenRetiredRendererCleanupThrows_RecordsBoundedCleanupDiagnostics()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var diagnostics = new RecordingVideoPlaybackDiagnostics();
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher,
            factory,
            new InMemoryStateStore(),
            diagnostics: diagnostics);
        await coordinator.ApplyAsync(Request("stable", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        factory.Plan("stable").StopFailure = new InvalidOperationException("retired stop failed");
        factory.Plan("stable").DisposeFailure = new InvalidOperationException("retired dispose failed");

        var result = await coordinator.ApplyAsync(
            Request("replacement", Target(FirstOutput, 101)),
            TestContext.Current.CancellationToken);

        Assert.Equal(AssignmentOutcome.Applied, result.Outcome);
        var stop = Assert.Single(diagnostics.Events, static entry => entry.Operation == "retired-stop");
        var dispose = Assert.Single(diagnostics.Events, static entry => entry.Operation == "retired-dispose");
        Assert.Equal("stable:DISPLAY-A", stop.RendererId);
        Assert.Equal("DISPLAY-A", stop.OutputKey);
        Assert.Equal(nameof(InvalidOperationException), stop.FailureCode);
        Assert.Equal(nameof(InvalidOperationException), dispose.FailureCode);
    }

    [Fact]
    public async Task SetReasonsAsync_WhenRendererMutatesThenThrows_RestoresOwnerReasonsAndPublishedPolicy()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var failure = new InvalidOperationException("mutate then throw");
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        await coordinator.ApplyAsync(Request("policy-failure", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        factory.Plan("policy-failure").PerformanceFailure = failure;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.SetReasonsAsync(
            FirstOutput,
            PerformanceReasonOwner.User,
            new HashSet<PerformanceReason> { PerformanceReason.UserPaused },
            TestContext.Current.CancellationToken));

        var output = Assert.Single(coordinator.GetSnapshot().Outputs);
        Assert.Empty(output.Reasons!);
        Assert.Equal(PerformanceState.Running, output.PerformanceState);
    }

    [Fact]
    public void AssignmentCommit_WhenAudioOwnerChangesToNonAudio_ClearsAudioOwner()
    {
        var video = VideoDefinition("video");
        var solid = Definition("solid");
        var initial = ValidState(
            [video, solid],
            [PersistedAssignment(FirstOutput, video, DisplayMode.Independent, null)],
            [],
            FirstOutput);

        var commit = AssignmentCommit.Create(initial, Request("solid", Target(FirstOutput, 101)));
        var validated = PersistedStateValidator.ValidateAndNormalize(commit.Next);

        Assert.Null(validated.AudioOwner);
    }

    [Fact]
    public void AssignmentCommit_WhenApplyingNewWallpaper_AddsDefinitionBeforeAssignment()
    {
        var request = Request("new-library-item", Target(FirstOutput, 101));

        var commit = AssignmentCommit.Create(PersistedState.Default, request);
        var validated = PersistedStateValidator.ValidateAndNormalize(commit.Next);

        Assert.Contains(validated.Library, item => item.Definition.Id == request.Wallpaper.Id);
        Assert.Equal(request.Wallpaper.Id, Assert.Single(validated.Assignments).Wallpaper);
    }

    [Fact]
    public void AssignmentCommit_WhenSameGroupIdGetsDisjointMembers_ReplacesOldGroupAndAssignments()
    {
        var groupId = new DisplayGroupId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"));
        var oldWallpaper = Definition("old-group");
        var newWallpaper = Definition("new-group");
        var third = new MonitorIdentity("DISPLAY-C");
        var fourth = new MonitorIdentity("DISPLAY-D");
        var initial = ValidState(
            [oldWallpaper, newWallpaper],
            [PersistedAssignment(FirstOutput, oldWallpaper, DisplayMode.Span, groupId),
             PersistedAssignment(SecondOutput, oldWallpaper, DisplayMode.Span, groupId)],
            [new PersistedDisplayGroup(groupId, DisplayMode.Span, oldWallpaper.Id, [FirstOutput, SecondOutput])],
            null);
        var request = new AssignmentRequest(
            newWallpaper,
            DisplayMode.Span,
            groupId,
            Canvas,
            [TargetAt(third, 303, 0), TargetAt(fourth, 404, 1920)]);

        var commit = AssignmentCommit.Create(initial, request);
        var validated = PersistedStateValidator.ValidateAndNormalize(commit.Next);

        var group = Assert.Single(validated.Groups);
        Assert.Equal(groupId, group.Id);
        Assert.Equal([third, fourth], group.Members);
        Assert.Equal([third, fourth], validated.Assignments.Select(static assignment => assignment.Monitor.Identity));
    }

    [Fact]
    public async Task ApplyAsync_AppliesExactOutputSettingsToRendererContextBeforeActivation()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var settings = new OutputWallpaperSettings(FitMode.Contain, 17, 42);
        var target = new OutputAssignmentTarget(
            FirstOutput,
            101,
            new DisplayViewport(0, 0, 1920, 1080),
            settings);

        await coordinator.ApplyAsync(Request("configured", target), TestContext.Current.CancellationToken);

        var renderer = factory.Renderer("configured");
        Assert.Equal(settings, renderer.Context!.Settings);
        Assert.Equal(RendererLifecycle.Active, renderer.Lifecycle);
    }

    [Fact]
    public async Task SelectAudioOwnerAsync_UsesOneGlobalTransactionAcrossIndependentOutputs()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(
            new AssignmentRequest(VideoDefinition("video-a"), DisplayMode.Independent, null, Canvas,
                [new OutputAssignmentTarget(FirstOutput, 101, Canvas, new OutputWallpaperSettings(FitMode.Cover, 30, 25))]),
            TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(
            new AssignmentRequest(VideoDefinition("video-b"), DisplayMode.Independent, null, Canvas,
                [new OutputAssignmentTarget(SecondOutput, 202, Canvas, new OutputWallpaperSettings(FitMode.Cover, 30, 75))]),
            TestContext.Current.CancellationToken);
        var first = factory.Renderer("video-a");
        var second = factory.Renderer("video-b");

        await coordinator.SelectAudioOwnerAsync(FirstOutput, TestContext.Current.CancellationToken);
        first.AudioEvents.Clear();
        second.AudioEvents.Clear();
        await coordinator.SelectAudioOwnerAsync(SecondOutput, TestContext.Current.CancellationToken);

        Assert.Equal(["mute"], first.AudioEvents);
        Assert.Equal(["volume:75", "unmute"], second.AudioEvents);
        Assert.True(first.IsMuted);
        Assert.False(second.IsMuted);
        Assert.Equal(SecondOutput, coordinator.GetSnapshot().State.AudioOwner);
        Assert.Equal(SecondOutput, state.State.AudioOwner);
    }

    [Fact]
    public async Task GroupRuntime_ComposesSharedClockPeriodicDriftAndImmediateResumeCorrection()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var time = new ManualGroupTimeProvider();
        var factory = new FakeWallpaperRendererFactory();
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher, factory, new InMemoryStateStore(), timeProvider: time);
        var groupId = new DisplayGroupId(Guid.Parse("dddddddd-dddd-dddd-dddd-dddddddddddd"));
        var video = VideoDefinition("synchronized-group");
        var request = new AssignmentRequest(
            video, DisplayMode.Duplicate, groupId, Canvas,
            [Target(FirstOutput, 101), Target(SecondOutput, 202)]);

        await coordinator.ApplyAsync(request, TestContext.Current.CancellationToken);
        var renderers = factory.Renderers("synchronized-group");
        Assert.Equal(2, renderers.Count);
        Assert.NotNull(renderers[0].ResumeObserver);
        Assert.Same(renderers[0].ResumeObserver, renderers[1].ResumeObserver);
        renderers[0].Position = TimeSpan.FromSeconds(0.02);
        renderers[1].Position = TimeSpan.FromSeconds(9.7);

        time.AdvanceAndFire(TimeSpan.FromSeconds(10.02));
        await renderers[1].SeekObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        await dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Empty(renderers[0].Seeks);
        Assert.Equal(TimeSpan.FromSeconds(0.02), Assert.Single(renderers[1].Seeks));

        time.AdvanceAndFire(TimeSpan.FromMilliseconds(200));
        await coordinator.SetReasonsAsync(
            SecondOutput, PerformanceReasonOwner.User,
            new HashSet<PerformanceReason> { PerformanceReason.UserPaused },
            TestContext.Current.CancellationToken);
        renderers[1].Position = TimeSpan.FromSeconds(9.9);
        await coordinator.SetReasonsAsync(
            SecondOutput, PerformanceReasonOwner.User,
            new HashSet<PerformanceReason>(),
            TestContext.Current.CancellationToken);
        await dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(2, renderers[1].Seeks.Count);
        Assert.Equal(TimeSpan.FromSeconds(0.22), renderers[1].Seeks[1]);
        renderers[1].Position = TimeSpan.Zero;
        time.AdvanceAndFire(TimeSpan.FromMilliseconds(800));
        await dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);
        Assert.Equal(2, renderers[1].Seeks.Count);
        await coordinator.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ApplyAsync_GroupSynchronizationSkipsWhenVideoDurationsDiffer()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var time = new ManualGroupTimeProvider();
        var factory = new FakeWallpaperRendererFactory();
        factory.Plan("mismatched-group").ReportedDurationResolver = host =>
            host == 101 ? TimeSpan.FromSeconds(10) : TimeSpan.FromSeconds(12);
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher, factory, new InMemoryStateStore(), timeProvider: time);
        var groupId = new DisplayGroupId(Guid.Parse("eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee"));
        var request = new AssignmentRequest(
            VideoDefinition("mismatched-group"), DisplayMode.Duplicate, groupId, Canvas,
            [Target(FirstOutput, 101), Target(SecondOutput, 202)]);

        await coordinator.ApplyAsync(request, TestContext.Current.CancellationToken);

        var renderers = factory.Renderers("mismatched-group");
        Assert.Equal(2, renderers.Count);
        Assert.Null(renderers[0].ResumeObserver);
        Assert.Null(renderers[1].ResumeObserver);
        renderers[0].Position = TimeSpan.FromSeconds(5);
        renderers[1].Position = TimeSpan.FromSeconds(9);

        time.AdvanceAndFire(TimeSpan.FromSeconds(1));
        await dispatcher.InvokeAsync(_ => ValueTask.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Empty(renderers[0].Seeks);
        Assert.Empty(renderers[1].Seeks);
        await coordinator.ShutdownAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ApplyAsync_WhenCallerCancelsDuringNonCooperativeSave_RestoresOldRendererAndState()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var state = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, state);
        await coordinator.ApplyAsync(Request("stable", Target(FirstOutput, 101)), TestContext.Current.CancellationToken);
        state.SaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.SaveRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        state.SaveObservesCancellation = false;
        using var cancellation = new CancellationTokenSource();
        var apply = coordinator.ApplyAsync(Request("candidate", Target(FirstOutput, 101)), cancellation.Token);
        await state.SaveStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        cancellation.Cancel();
        state.SaveRelease.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => apply);

        Assert.Equal("stable", factory.Active(101)!.Name);
        Assert.Equal(Definition("stable").Id, state.State.Assignments.Single().Wallpaper);
        Assert.Equal(1, factory.Renderer("candidate").DisposeCount);
    }

    [Fact]
    public async Task ApplyAsync_GroupDisconnectDuringSwap_ReturnsHostUnavailableDiagnostic()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        var activation = new RendererBarrier(observeCancellation: false);
        factory.Plan("group").ActivationBarrier = activation;
        factory.Plan("group").ActivationBarrierHost = 202;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());
        var apply = coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken);
        await activation.Started.Task.WaitAsync(TestContext.Current.CancellationToken);

        var disconnect = coordinator.SetReasonsAsync(
            SecondOutput,
            PerformanceReasonOwner.Topology,
            new HashSet<PerformanceReason> { PerformanceReason.MonitorDisconnected },
            TestContext.Current.CancellationToken);
        await factory.Renderers("group")[1].SuspendedSet.Task.WaitAsync(TestContext.Current.CancellationToken);
        activation.Release();
        var result = await apply;
        await disconnect;

        Assert.Equal(AssignmentOutcome.Superseded, result.Outcome);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Output == SecondOutput && diagnostic.Code == AssignmentDiagnosticCode.HostUnavailable);
    }

    [Fact]
    public async Task ApplyAsync_GroupRollbackStopFailsWithoutOldRenderer_ReturnsRollbackDiagnostic()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var factory = new FakeWallpaperRendererFactory();
        factory.Plan("group").ActivationFailure = new InvalidOperationException("second swap failed");
        factory.Plan("group").ActivationFailureHost = 202;
        factory.Plan("group").StopFailure = new InvalidOperationException("first rollback stop failed");
        factory.Plan("group").StopFailureHost = 101;
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, new InMemoryStateStore());

        var failure = await Assert.ThrowsAsync<WallpaperActivationException>(
            () => coordinator.ApplyAsync(GroupRequest("group"), TestContext.Current.CancellationToken));

        Assert.Contains(failure.Diagnostics, diagnostic =>
            diagnostic.Output == FirstOutput && diagnostic.Code == AssignmentDiagnosticCode.RollbackFailed);
    }

    private static AssignmentRequest Request(string name, OutputAssignmentTarget target) =>
        new(Definition(name), DisplayMode.Independent, null, Canvas, [target]);

    private static AssignmentRequest GroupRequest(string name) =>
        new(Definition(name), DisplayMode.Span, new DisplayGroupId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")), Canvas,
            [Target(FirstOutput, 101), Target(SecondOutput, 202)]);

    private static OutputAssignmentTarget Target(MonitorIdentity monitor, nint host) =>
        new(monitor, host, monitor == FirstOutput ? new DisplayViewport(0, 0, 1920, 1080) : new DisplayViewport(1920, 0, 1920, 1080),
            new OutputWallpaperSettings(FitMode.Cover, 30, 0));

    private static OutputAssignmentTarget TargetAt(MonitorIdentity monitor, nint host, int x) =>
        new(monitor, host, new DisplayViewport(x, 0, 1920, 1080),
            new OutputWallpaperSettings(FitMode.Cover, 30, 0));

    private static WallpaperDefinition Definition(string name) => new(
        new WallpaperId(DeterministicGuid(name)),
        name,
        SolidColorSource.Create("#123456"),
        FitMode.Cover,
        30,
        false,
        false,
        0,
        false);

    private static WallpaperDefinition VideoDefinition(string name) => new(
        new WallpaperId(DeterministicGuid(name)),
        name,
        VideoSource.Create(Path.GetFullPath($"{name}.mp4")),
        FitMode.Contain,
        24,
        false,
        true,
        50,
        false);

    private static PersistedState ValidState(
        IReadOnlyList<WallpaperDefinition> definitions,
        IReadOnlyList<WallpaperAssignment> assignments,
        IReadOnlyList<PersistedDisplayGroup> groups,
        MonitorIdentity? audioOwner) =>
        new(
            1,
            definitions.Select(definition => new WallpaperLibraryItem(
                definition,
                null,
                definition.Source is VideoSource ? new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(10), 30, "h264", true) : null,
                new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UnixEpoch))).ToArray(),
            assignments,
            groups,
            audioOwner);

    private static WallpaperAssignment PersistedAssignment(
        MonitorIdentity monitor,
        WallpaperDefinition wallpaper,
        DisplayMode mode,
        DisplayGroupId? groupId)
    {
        var viewport = monitor == FirstOutput
            ? new DisplayViewport(0, 0, 1920, 1080)
            : new DisplayViewport(1920, 0, 1920, 1080);
        var evidence = new MonitorIdentityEvidence(0, 0, 0, null, null, null, null, null, null, monitor.Key, viewport);
        return new WallpaperAssignment(
            new PersistedMonitorReference(monitor, evidence),
            wallpaper.Id,
            mode,
            wallpaper.Fit,
            wallpaper.TargetFps,
            wallpaper.VolumePercent,
            groupId);
    }

    private static Guid DeterministicGuid(string value)
    {
        var bytes = new byte[16];
        var source = System.Text.Encoding.UTF8.GetBytes(value);
        for (var index = 0; index < source.Length; index++) bytes[index % bytes.Length] ^= source[index];
        bytes[15] |= 1;
        return new Guid(bytes);
    }

    private sealed class ManualGroupTimeProvider : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(callback, state, dueTime, period);
            _timers.Add(timer);
            return timer;
        }

        public void AdvanceAndFire(TimeSpan duration)
        {
            _timestamp += duration.Ticks;
            foreach (var timer in _timers.ToArray()) timer.Fire(duration);
        }

        private sealed class ManualTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
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

            public void Fire(TimeSpan elapsed)
            {
                if (_disposed || _remaining == Timeout.InfiniteTimeSpan) return;
                _remaining -= elapsed;
                if (_remaining > TimeSpan.Zero) return;
                callback(state);
                _remaining = _period;
            }

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
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
