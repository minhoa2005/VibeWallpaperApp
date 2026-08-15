using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Core.Rendering;

public static class SpanLayoutCalculator
{
    public static SpanLayout Calculate(IReadOnlyList<MonitorDescriptor> monitors)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0 || monitors.Any(static monitor => monitor is null))
            throw new ArgumentException("At least one non-null monitor is required.", nameof(monitors));
        if (monitors.Select(static monitor => monitor.Identity.Key).Distinct(StringComparer.Ordinal).Count() != monitors.Count)
            throw new ArgumentException("Span monitors must be unique.", nameof(monitors));

        var left = monitors.Min(static monitor => (long)monitor.Bounds.X);
        var top = monitors.Min(static monitor => (long)monitor.Bounds.Y);
        var right = monitors.Max(static monitor => (long)monitor.Bounds.X + monitor.Bounds.Width);
        var bottom = monitors.Max(static monitor => (long)monitor.Bounds.Y + monitor.Bounds.Height);
        var width = checked((int)(right - left));
        var height = checked((int)(bottom - top));
        var canvas = new DisplayViewport(checked((int)left), checked((int)top), width, height);
        var viewports = monitors.Select(monitor =>
        {
            var bounds = monitor.Bounds;
            var crop = new NormalizedSourceRect(
                ((long)bounds.X - left) / (double)width,
                ((long)bounds.Y - top) / (double)height,
                bounds.Width / (double)width,
                bounds.Height / (double)height);
            return new SpanViewport(monitor.Identity, canvas, bounds, crop);
        }).ToArray();
        return new SpanLayout(canvas, viewports);
    }
}
