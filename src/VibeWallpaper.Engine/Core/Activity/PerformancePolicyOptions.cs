using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Core.Activity;

public enum RendererThrottleCapability
{
    Cooperative,
    Unsupported,
}

public enum IncompatibleThrottleBehavior
{
    Continue,
    Suspend,
}

public sealed record PerformancePolicyOptions
{
    public static PerformancePolicyOptions Default { get; } =
        new(true, false, true, true, true, true, 30, 15, IncompatibleThrottleBehavior.Continue);

    public bool SuspendOnFullscreen { get; }
    public bool SuspendOnMaximized { get; }
    public bool SuspendOnRemoteDesktop { get; }
    public bool SuspendOnSessionLock { get; }
    public bool SuspendOnDisplayOff { get; }
    public bool SuspendOnSystemSleep { get; }
    public int BatteryTargetFps { get; }
    public int BatterySaverTargetFps { get; }
    public IncompatibleThrottleBehavior IncompatibleThrottle { get; }

    public PerformancePolicyOptions(
        bool suspendOnFullscreen,
        bool suspendOnMaximized,
        bool suspendOnRemoteDesktop,
        bool suspendOnSessionLock,
        bool suspendOnDisplayOff,
        bool suspendOnSystemSleep,
        int batteryTargetFps,
        int batterySaverTargetFps,
        IncompatibleThrottleBehavior incompatibleThrottle)
    {
        ValidateFps(batteryTargetFps, nameof(batteryTargetFps));
        ValidateFps(batterySaverTargetFps, nameof(batterySaverTargetFps));
        if (!Enum.IsDefined(incompatibleThrottle))
        {
            throw new ArgumentException("A defined incompatible throttle behavior is required.", nameof(incompatibleThrottle));
        }

        SuspendOnFullscreen = suspendOnFullscreen;
        SuspendOnMaximized = suspendOnMaximized;
        SuspendOnRemoteDesktop = suspendOnRemoteDesktop;
        SuspendOnSessionLock = suspendOnSessionLock;
        SuspendOnDisplayOff = suspendOnDisplayOff;
        SuspendOnSystemSleep = suspendOnSystemSleep;
        BatteryTargetFps = batteryTargetFps;
        BatterySaverTargetFps = batterySaverTargetFps;
        IncompatibleThrottle = incompatibleThrottle;
    }

    internal static void ValidateFps(int targetFps, string parameterName)
    {
        if (targetFps is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(parameterName, "Target FPS must be between 1 and 60.");
        }
    }
}

public sealed record PerformancePolicy
{
    public MonitorIdentity Output { get; }
    public PerformanceState State { get; }
    public int? TargetFps { get; }

    public PerformancePolicy(MonitorIdentity output, PerformanceState state, int? targetFps)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentException("A defined performance state is required.", nameof(state));
        }

        if (state == PerformanceState.Throttled)
        {
            if (!targetFps.HasValue)
            {
                throw new ArgumentException("A throttled policy requires a target FPS.", nameof(targetFps));
            }

            PerformancePolicyOptions.ValidateFps(targetFps.Value, nameof(targetFps));
        }
        else if (targetFps.HasValue)
        {
            throw new ArgumentException("Only throttled policies may specify a target FPS.", nameof(targetFps));
        }

        Output = output;
        State = state;
        TargetFps = targetFps;
    }
}
