using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Core.Activity;

public static class WindowCoverageClassifier
{
    public const double DefaultFullscreenThreshold = 0.98d;

    public const int DefaultPhysicalEdgeTolerance = 2;

    public static WindowCoverage Classify(
        WindowSnapshot window,
        MonitorDescriptor monitor,
        double fullscreenThreshold = DefaultFullscreenThreshold,
        int physicalEdgeTolerance = DefaultPhysicalEdgeTolerance)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(monitor);
        ValidateOptions(fullscreenThreshold, physicalEdgeTolerance);

        var intersectionArea = IntersectionArea(window.ExtendedFrameBounds, monitor.Bounds);
        var monitorArea = Area(monitor.Bounds);
        var fraction = intersectionArea / (double)monitorArea;
        if (intersectionArea == 0)
        {
            return new WindowCoverage(monitor.Identity, window.Hwnd, CoverageKind.None, 0);
        }

        if (fraction >= fullscreenThreshold &&
            LeavesNoEdgeGapLargerThan(window.ExtendedFrameBounds, monitor.Bounds, physicalEdgeTolerance))
        {
            return new WindowCoverage(monitor.Identity, window.Hwnd, CoverageKind.Fullscreen, fraction);
        }

        var workAreaFraction = IntersectionArea(window.ExtendedFrameBounds, monitor.WorkArea) /
            (double)Area(monitor.WorkArea);
        if (workAreaFraction >= fullscreenThreshold &&
            LeavesNoEdgeGapLargerThan(window.ExtendedFrameBounds, monitor.WorkArea, physicalEdgeTolerance))
        {
            return new WindowCoverage(monitor.Identity, window.Hwnd, CoverageKind.MaximizedWorkArea, fraction);
        }

        return new WindowCoverage(monitor.Identity, window.Hwnd, CoverageKind.Partial, fraction);
    }

    public static IReadOnlyList<WindowCoverage> Classify(
        IReadOnlyList<WindowSnapshot> windows,
        IReadOnlyList<MonitorDescriptor> monitors,
        double fullscreenThreshold = DefaultFullscreenThreshold,
        int physicalEdgeTolerance = DefaultPhysicalEdgeTolerance)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(monitors);
        ValidateOptions(fullscreenThreshold, physicalEdgeTolerance);
        if (windows.Any(static window => window is null))
        {
            throw new ArgumentException("Windows cannot contain null.", nameof(windows));
        }

        if (monitors.Any(static monitor => monitor is null))
        {
            throw new ArgumentException("Monitors cannot contain null.", nameof(monitors));
        }

        var orderedWindows = windows.OrderBy(static window => window.ZOrder).ToArray();
        var results = new List<WindowCoverage>(monitors.Count);
        foreach (var monitor in monitors)
        {
            var topmost = orderedWindows.FirstOrDefault(
                window => IntersectionArea(window.ExtendedFrameBounds, monitor.Bounds) > 0);
            results.Add(topmost is null
                ? new WindowCoverage(monitor.Identity, 0, CoverageKind.None, 0)
                : Classify(topmost, monitor, fullscreenThreshold, physicalEdgeTolerance));
        }

        return new ReadOnlyCollection<WindowCoverage>(results);
    }

    private static long IntersectionArea(DisplayViewport first, DisplayViewport second)
    {
        var left = Math.Max((long)first.X, second.X);
        var top = Math.Max((long)first.Y, second.Y);
        var right = Math.Min((long)first.X + first.Width, (long)second.X + second.Width);
        var bottom = Math.Min((long)first.Y + first.Height, (long)second.Y + second.Height);
        return Math.Max(0, right - left) * Math.Max(0, bottom - top);
    }

    private static long Area(DisplayViewport rectangle) => (long)rectangle.Width * rectangle.Height;

    private static bool LeavesNoEdgeGapLargerThan(
        DisplayViewport window,
        DisplayViewport target,
        int tolerance)
    {
        var windowRight = (long)window.X + window.Width;
        var windowBottom = (long)window.Y + window.Height;
        var targetRight = (long)target.X + target.Width;
        var targetBottom = (long)target.Y + target.Height;
        return window.X <= (long)target.X + tolerance &&
            window.Y <= (long)target.Y + tolerance &&
            windowRight >= targetRight - tolerance &&
            windowBottom >= targetBottom - tolerance;
    }

    private static void ValidateOptions(double fullscreenThreshold, int physicalEdgeTolerance)
    {
        if (!double.IsFinite(fullscreenThreshold) || fullscreenThreshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fullscreenThreshold));
        }

        if (physicalEdgeTolerance < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(physicalEdgeTolerance));
        }
    }
}
