using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Tests.Core;

public sealed class PerformancePolicyEvaluatorTests
{
    private static readonly MonitorIdentity Output = new("DISPLAY-A");

    [Theory]
    [InlineData(PerformanceReason.Battery, PerformanceState.Throttled)]
    [InlineData(PerformanceReason.FullscreenCovered, PerformanceState.Suspended)]
    [InlineData(PerformanceReason.SessionLocked, PerformanceState.Suspended)]
    public void Evaluate_MapsReasonToExpectedState(PerformanceReason reason, PerformanceState expected)
    {
        var result = Evaluate([reason]);

        Assert.Equal(expected, result.State);
    }

    [Fact]
    public void Evaluate_SuspensionWinsOverBatteryThrottling()
    {
        var result = Evaluate([PerformanceReason.Battery, PerformanceReason.FullscreenCovered]);

        Assert.Equal(PerformanceState.Suspended, result.State);
        Assert.Null(result.TargetFps);
    }

    [Fact]
    public void Evaluate_CoveredOutputOnly_DoesNotSuspendOtherOutput()
    {
        var covered = Evaluate([PerformanceReason.FullscreenCovered], new MonitorIdentity("A"));
        var other = Evaluate([], new MonitorIdentity("B"));

        Assert.Equal(PerformanceState.Suspended, covered.State);
        Assert.Equal(PerformanceState.Running, other.State);
    }

    [Theory]
    [InlineData(PerformanceReason.FullscreenCovered)]
    [InlineData(PerformanceReason.MaximizedCovered)]
    [InlineData(PerformanceReason.RemoteDesktop)]
    [InlineData(PerformanceReason.SessionLocked)]
    [InlineData(PerformanceReason.DisplayOff)]
    [InlineData(PerformanceReason.SystemSleeping)]
    public void Evaluate_EnabledConditionalReason_Suspends(PerformanceReason reason)
    {
        var result = Evaluate([reason], options: OptionsWith(reason, true));

        Assert.Equal(PerformanceState.Suspended, result.State);
    }

    [Theory]
    [InlineData(PerformanceReason.FullscreenCovered)]
    [InlineData(PerformanceReason.MaximizedCovered)]
    [InlineData(PerformanceReason.RemoteDesktop)]
    [InlineData(PerformanceReason.SessionLocked)]
    [InlineData(PerformanceReason.DisplayOff)]
    [InlineData(PerformanceReason.SystemSleeping)]
    public void Evaluate_DisabledConditionalReason_DoesNotSuspendByItself(PerformanceReason reason)
    {
        var result = Evaluate([reason], options: OptionsWith(reason, false));

        Assert.Equal(PerformanceState.Running, result.State);
        Assert.Null(result.TargetFps);
    }

    [Theory]
    [InlineData(PerformanceReason.UserPaused)]
    [InlineData(PerformanceReason.RendererFault)]
    [InlineData(PerformanceReason.ExplorerUnavailable)]
    [InlineData(PerformanceReason.MonitorDisconnected)]
    [InlineData(PerformanceReason.Shutdown)]
    public void Evaluate_UnconditionalSafetyReason_AlwaysSuspends(PerformanceReason reason)
    {
        var result = Evaluate([reason], options: AllConditionalOptions(false));

        Assert.Equal(PerformanceState.Suspended, result.State);
        Assert.Null(result.TargetFps);
    }

    [Fact]
    public void Evaluate_BatterySaverTakesPrecedenceAndUsesConfiguredFps()
    {
        var options = new PerformancePolicyOptions(true, false, true, true, true, true, 37, 12, IncompatibleThrottleBehavior.Continue);

        var result = Evaluate([PerformanceReason.Battery, PerformanceReason.BatterySaver], options: options);

        Assert.Equal(PerformanceState.Throttled, result.State);
        Assert.Equal(12, result.TargetFps);
    }

    [Fact]
    public void Evaluate_BatteryUsesConfiguredFps()
    {
        var options = new PerformancePolicyOptions(true, false, true, true, true, true, 37, 12, IncompatibleThrottleBehavior.Continue);

        var result = Evaluate([PerformanceReason.Battery], options: options);

        Assert.Equal(37, result.TargetFps);
    }

    [Theory]
    [InlineData(IncompatibleThrottleBehavior.Continue, PerformanceState.Running)]
    [InlineData(IncompatibleThrottleBehavior.Suspend, PerformanceState.Suspended)]
    public void Evaluate_UnsupportedThrottleUsesConfiguredBehavior(IncompatibleThrottleBehavior behavior, PerformanceState expected)
    {
        var result = Evaluate(
            [PerformanceReason.Battery],
            options: new PerformancePolicyOptions(true, false, true, true, true, true, 30, 15, behavior),
            capability: RendererThrottleCapability.Unsupported);

        Assert.Equal(expected, result.State);
        Assert.Null(result.TargetFps);
    }

    [Fact]
    public void Evaluate_EmptyAndDuplicateReasons_AreDeterministicAndDoNotMutateInput()
    {
        var reasons = new List<PerformanceReason> { PerformanceReason.Battery, PerformanceReason.Battery };

        var first = Evaluate(reasons);
        var second = Evaluate(reasons);
        var none = Evaluate([]);

        Assert.Equal(first, second);
        Assert.Equal([PerformanceReason.Battery, PerformanceReason.Battery], reasons);
        Assert.Equal(PerformanceState.Throttled, first.State);
        Assert.Equal(PerformanceState.Running, none.State);
    }

    [Fact]
    public void Evaluate_RejectsNullAndUndefinedArguments()
    {
        Assert.Throws<ArgumentNullException>(() => PerformancePolicyEvaluator.Evaluate(null!, [], PerformancePolicyOptions.Default, RendererThrottleCapability.Cooperative));
        Assert.Throws<ArgumentNullException>(() => PerformancePolicyEvaluator.Evaluate(Output, null!, PerformancePolicyOptions.Default, RendererThrottleCapability.Cooperative));
        Assert.Throws<ArgumentNullException>(() => PerformancePolicyEvaluator.Evaluate(Output, [], null!, RendererThrottleCapability.Cooperative));
        Assert.Throws<ArgumentException>(() => PerformancePolicyEvaluator.Evaluate(Output, [(PerformanceReason)99], PerformancePolicyOptions.Default, RendererThrottleCapability.Cooperative));
        Assert.Throws<ArgumentException>(() => PerformancePolicyEvaluator.Evaluate(Output, [], PerformancePolicyOptions.Default, (RendererThrottleCapability)99));
    }

    [Fact]
    public void Options_DefaultAndConstructorEnforceInvariants()
    {
        Assert.True(PerformancePolicyOptions.Default.SuspendOnFullscreen);
        Assert.False(PerformancePolicyOptions.Default.SuspendOnMaximized);
        Assert.True(PerformancePolicyOptions.Default.SuspendOnRemoteDesktop);
        Assert.True(PerformancePolicyOptions.Default.SuspendOnSessionLock);
        Assert.True(PerformancePolicyOptions.Default.SuspendOnDisplayOff);
        Assert.True(PerformancePolicyOptions.Default.SuspendOnSystemSleep);
        Assert.Equal(30, PerformancePolicyOptions.Default.BatteryTargetFps);
        Assert.Equal(15, PerformancePolicyOptions.Default.BatterySaverTargetFps);
        Assert.Equal(IncompatibleThrottleBehavior.Continue, PerformancePolicyOptions.Default.IncompatibleThrottle);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerformancePolicyOptions(true, false, true, true, true, true, 0, 15, IncompatibleThrottleBehavior.Continue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerformancePolicyOptions(true, false, true, true, true, true, 30, 61, IncompatibleThrottleBehavior.Continue));
        Assert.Throws<ArgumentException>(() => new PerformancePolicyOptions(true, false, true, true, true, true, 30, 15, (IncompatibleThrottleBehavior)99));
    }

    [Fact]
    public void Policy_EnforcesOutputStateAndTargetInvariants()
    {
        Assert.Throws<ArgumentNullException>(() => new PerformancePolicy(null!, PerformanceState.Running, null));
        Assert.Throws<ArgumentException>(() => new PerformancePolicy(Output, (PerformanceState)99, null));
        Assert.Throws<ArgumentException>(() => new PerformancePolicy(Output, PerformanceState.Running, 30));
        Assert.Throws<ArgumentException>(() => new PerformancePolicy(Output, PerformanceState.Throttled, null));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PerformancePolicy(Output, PerformanceState.Throttled, 0));
        Assert.Throws<ArgumentException>(() => new PerformancePolicy(Output, PerformanceState.Suspended, 30));
    }

    [Fact]
    public void ReasonSet_AddRemoveAreIdempotentAndReturnAssociationChanges()
    {
        var reasons = new PerformanceReasonSet();

        Assert.True(reasons.Add(PerformanceReasonOwner.Activity, PerformanceReason.Battery));
        Assert.False(reasons.Add(PerformanceReasonOwner.Activity, PerformanceReason.Battery));
        Assert.True(reasons.Remove(PerformanceReasonOwner.Activity, PerformanceReason.Battery));
        Assert.False(reasons.Remove(PerformanceReasonOwner.Activity, PerformanceReason.Battery));
    }

    [Fact]
    public void ReasonSet_SameReasonOwnedTwice_RemainsUntilEveryOwnerReleasesIt()
    {
        var reasons = new PerformanceReasonSet();
        reasons.Add(PerformanceReasonOwner.Activity, PerformanceReason.UserPaused);
        reasons.Add(PerformanceReasonOwner.User, PerformanceReason.UserPaused);

        reasons.Remove(PerformanceReasonOwner.Activity, PerformanceReason.UserPaused);
        Assert.Contains(PerformanceReason.UserPaused, (IEnumerable<PerformanceReason>)reasons.Snapshot());
        Assert.Equal(PerformanceState.Suspended, Evaluate(reasons.Snapshot()).State);
        reasons.Remove(PerformanceReasonOwner.User, PerformanceReason.UserPaused);

        Assert.DoesNotContain(PerformanceReason.UserPaused, (IEnumerable<PerformanceReason>)reasons.Snapshot());
        Assert.Equal(PerformanceState.Running, Evaluate(reasons.Snapshot()).State);
    }

    [Fact]
    public void ReasonSet_ReplaceIsIdempotentDeduplicatesAndDoesNotClearOtherOwners()
    {
        var reasons = new PerformanceReasonSet();
        reasons.Add(PerformanceReasonOwner.User, PerformanceReason.UserPaused);

        Assert.False(reasons.ReplaceOwnedReasons(PerformanceReasonOwner.Activity, []));
        Assert.True(reasons.ReplaceOwnedReasons(PerformanceReasonOwner.Activity, [PerformanceReason.Battery, PerformanceReason.Battery]));
        Assert.False(reasons.ReplaceOwnedReasons(PerformanceReasonOwner.Activity, [PerformanceReason.Battery]));
        Assert.True(reasons.ReplaceOwnedReasons(PerformanceReasonOwner.Activity, []));

        var snapshot = reasons.Snapshot();
        Assert.DoesNotContain(PerformanceReason.Battery, (IEnumerable<PerformanceReason>)snapshot);
        Assert.Contains(PerformanceReason.UserPaused, (IEnumerable<PerformanceReason>)snapshot);
    }

    [Fact]
    public void ReasonSet_SnapshotIsAnImmutableCopy()
    {
        var reasons = new PerformanceReasonSet();
        reasons.Add(PerformanceReasonOwner.Activity, PerformanceReason.Battery);
        var snapshot = reasons.Snapshot();

        reasons.Add(PerformanceReasonOwner.Activity, PerformanceReason.UserPaused);

        Assert.Contains(PerformanceReason.Battery, (IEnumerable<PerformanceReason>)snapshot);
        Assert.DoesNotContain(PerformanceReason.UserPaused, (IEnumerable<PerformanceReason>)snapshot);
    }

    [Fact]
    public void ReasonSet_RejectsInvalidValuesAndNullReplacement()
    {
        var reasons = new PerformanceReasonSet();

        Assert.Throws<ArgumentException>(() => reasons.Add((PerformanceReasonOwner)99, PerformanceReason.Battery));
        Assert.Throws<ArgumentException>(() => reasons.Add(PerformanceReasonOwner.Activity, (PerformanceReason)99));
        Assert.Throws<ArgumentException>(() => reasons.Remove((PerformanceReasonOwner)99, PerformanceReason.Battery));
        Assert.Throws<ArgumentException>(() => reasons.Remove(PerformanceReasonOwner.Activity, (PerformanceReason)99));
        Assert.Throws<ArgumentException>(() => reasons.ReplaceOwnedReasons((PerformanceReasonOwner)99, []));
        Assert.Throws<ArgumentNullException>(() => reasons.ReplaceOwnedReasons(PerformanceReasonOwner.Activity, null!));
        Assert.Throws<ArgumentException>(() => reasons.ReplaceOwnedReasons(PerformanceReasonOwner.Activity, [(PerformanceReason)99]));
    }

    [Fact]
    public void ActivitySnapshot_DefensivelyCopiesOutputsAndUsesStructuralIdentityEquality()
    {
        var fullscreen = new List<MonitorIdentity> { new("DISPLAY-A") };
        var maximized = new List<MonitorIdentity> { new("DISPLAY-B") };
        var snapshot = new ActivitySnapshot(true, true, true, true, true, true, fullscreen, maximized);
        fullscreen.Clear();
        maximized.Add(new MonitorIdentity("DISPLAY-C"));

        Assert.True(snapshot.SessionLocked);
        Assert.True(snapshot.DisplayOff);
        Assert.True(snapshot.SystemSleeping);
        Assert.True(snapshot.RunningOnBattery);
        Assert.True(snapshot.BatterySaverEnabled);
        Assert.True(snapshot.RemoteDesktopSession);
        Assert.Contains(new MonitorIdentity("DISPLAY-A"), (IEnumerable<MonitorIdentity>)snapshot.FullscreenCoveredOutputs);
        Assert.Contains(new MonitorIdentity("DISPLAY-B"), (IEnumerable<MonitorIdentity>)snapshot.MaximizedOutputs);
        Assert.DoesNotContain(new MonitorIdentity("DISPLAY-C"), (IEnumerable<MonitorIdentity>)snapshot.MaximizedOutputs);
    }

    [Fact]
    public void ActivitySnapshot_RejectsNullEnumerablesAndElements()
    {
        Assert.Throws<ArgumentNullException>(() => new ActivitySnapshot(false, false, false, false, false, false, null!, []));
        Assert.Throws<ArgumentNullException>(() => new ActivitySnapshot(false, false, false, false, false, false, [], null!));
        Assert.Throws<ArgumentException>(() => new ActivitySnapshot(false, false, false, false, false, false, [null!], []));
    }

    private static PerformancePolicy Evaluate(
        IEnumerable<PerformanceReason> reasons,
        MonitorIdentity? output = null,
        PerformancePolicyOptions? options = null,
        RendererThrottleCapability capability = RendererThrottleCapability.Cooperative) =>
        PerformancePolicyEvaluator.Evaluate(output ?? Output, reasons, options ?? PerformancePolicyOptions.Default, capability);

    private static PerformancePolicyOptions OptionsWith(PerformanceReason reason, bool enabled) => reason switch
    {
        PerformanceReason.FullscreenCovered => new(enabled, false, false, false, false, false, 30, 15, IncompatibleThrottleBehavior.Continue),
        PerformanceReason.MaximizedCovered => new(false, enabled, false, false, false, false, 30, 15, IncompatibleThrottleBehavior.Continue),
        PerformanceReason.RemoteDesktop => new(false, false, enabled, false, false, false, 30, 15, IncompatibleThrottleBehavior.Continue),
        PerformanceReason.SessionLocked => new(false, false, false, enabled, false, false, 30, 15, IncompatibleThrottleBehavior.Continue),
        PerformanceReason.DisplayOff => new(false, false, false, false, enabled, false, 30, 15, IncompatibleThrottleBehavior.Continue),
        PerformanceReason.SystemSleeping => new(false, false, false, false, false, enabled, 30, 15, IncompatibleThrottleBehavior.Continue),
        _ => throw new ArgumentOutOfRangeException(nameof(reason)),
    };

    private static PerformancePolicyOptions AllConditionalOptions(bool enabled) =>
        new(enabled, enabled, enabled, enabled, enabled, enabled, 30, 15, IncompatibleThrottleBehavior.Continue);
}
