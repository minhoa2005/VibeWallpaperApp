using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.App.ViewModels;

public sealed record DashboardRect(double Left, double Top, double Width, double Height);

public static class MonitorTopologyLayout
{
    public static IReadOnlyDictionary<MonitorIdentity, DashboardRect> Arrange(
        IReadOnlyList<MonitorDescriptor> monitors,
        double availableWidth,
        double availableHeight,
        double margin)
    {
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) return new ReadOnlyDictionary<MonitorIdentity, DashboardRect>(new Dictionary<MonitorIdentity, DashboardRect>());
        if (!double.IsFinite(availableWidth) || !double.IsFinite(availableHeight) || availableWidth <= margin * 2 || availableHeight <= margin * 2)
            throw new ArgumentOutOfRangeException(nameof(availableWidth));
        if (!double.IsFinite(margin) || margin < 0) throw new ArgumentOutOfRangeException(nameof(margin));
        if (monitors.Any(static monitor => monitor is null)) throw new ArgumentException("Monitors cannot contain null.", nameof(monitors));

        var minX = monitors.Min(static monitor => monitor.Bounds.X);
        var minY = monitors.Min(static monitor => monitor.Bounds.Y);
        var maxX = monitors.Max(static monitor => (long)monitor.Bounds.X + monitor.Bounds.Width);
        var maxY = monitors.Max(static monitor => (long)monitor.Bounds.Y + monitor.Bounds.Height);
        var virtualWidth = maxX - minX;
        var virtualHeight = maxY - minY;
        var scale = Math.Min((availableWidth - 2 * margin) / virtualWidth, (availableHeight - 2 * margin) / virtualHeight);
        var usedWidth = virtualWidth * scale;
        var usedHeight = virtualHeight * scale;
        var offsetX = margin + (availableWidth - 2 * margin - usedWidth) / 2;
        var offsetY = margin + (availableHeight - 2 * margin - usedHeight) / 2;

        return new ReadOnlyDictionary<MonitorIdentity, DashboardRect>(monitors.ToDictionary(
            static monitor => monitor.Identity,
            monitor => new DashboardRect(
                offsetX + (monitor.Bounds.X - minX) * scale,
                offsetY + (monitor.Bounds.Y - minY) * scale,
                monitor.Bounds.Width * scale,
                monitor.Bounds.Height * scale)));
    }
}
