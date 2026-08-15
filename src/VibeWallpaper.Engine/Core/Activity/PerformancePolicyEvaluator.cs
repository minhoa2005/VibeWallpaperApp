using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Core.Activity;

public static class PerformancePolicyEvaluator
{
    public static PerformancePolicy Evaluate(
        MonitorIdentity output,
        IEnumerable<PerformanceReason> reasons,
        PerformancePolicyOptions options,
        RendererThrottleCapability throttleCapability)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(reasons);
        ArgumentNullException.ThrowIfNull(options);
        if (!Enum.IsDefined(throttleCapability))
        {
            throw new ArgumentException("A defined renderer throttle capability is required.", nameof(throttleCapability));
        }

        var effectiveReasons = new HashSet<PerformanceReason>();
        foreach (var reason in reasons)
        {
            if (!Enum.IsDefined(reason))
            {
                throw new ArgumentException("A defined performance reason is required.", nameof(reasons));
            }

            effectiveReasons.Add(reason);
        }

        if (HasSuspensionReason(effectiveReasons, options))
        {
            return new PerformancePolicy(output, PerformanceState.Suspended, null);
        }

        if (effectiveReasons.Contains(PerformanceReason.BatterySaver))
        {
            return EvaluateThrottle(output, options.BatterySaverTargetFps, options, throttleCapability);
        }

        if (effectiveReasons.Contains(PerformanceReason.Battery))
        {
            return EvaluateThrottle(output, options.BatteryTargetFps, options, throttleCapability);
        }

        return new PerformancePolicy(output, PerformanceState.Running, null);
    }

    private static PerformancePolicy EvaluateThrottle(
        MonitorIdentity output,
        int targetFps,
        PerformancePolicyOptions options,
        RendererThrottleCapability capability) => capability switch
    {
        RendererThrottleCapability.Cooperative => new PerformancePolicy(output, PerformanceState.Throttled, targetFps),
        RendererThrottleCapability.Unsupported when options.IncompatibleThrottle == IncompatibleThrottleBehavior.Continue =>
            new PerformancePolicy(output, PerformanceState.Running, null),
        RendererThrottleCapability.Unsupported => new PerformancePolicy(output, PerformanceState.Suspended, null),
        _ => throw new ArgumentOutOfRangeException(nameof(capability)),
    };

    private static bool HasSuspensionReason(
        IReadOnlySet<PerformanceReason> reasons,
        PerformancePolicyOptions options) =>
        reasons.Overlaps(
        [
            PerformanceReason.UserPaused,
            PerformanceReason.RendererFault,
            PerformanceReason.ExplorerUnavailable,
            PerformanceReason.MonitorDisconnected,
            PerformanceReason.Shutdown,
        ]) ||
        (options.SuspendOnFullscreen && reasons.Contains(PerformanceReason.FullscreenCovered)) ||
        (options.SuspendOnMaximized && reasons.Contains(PerformanceReason.MaximizedCovered)) ||
        (options.SuspendOnRemoteDesktop && reasons.Contains(PerformanceReason.RemoteDesktop)) ||
        (options.SuspendOnSessionLock && reasons.Contains(PerformanceReason.SessionLocked)) ||
        (options.SuspendOnDisplayOff && reasons.Contains(PerformanceReason.DisplayOff)) ||
        (options.SuspendOnSystemSleep && reasons.Contains(PerformanceReason.SystemSleeping));
}
