using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Core.Rendering;

public sealed record NormalizedSourceRect
{
    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }

    public NormalizedSourceRect(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) ||
            !double.IsFinite(width) || !double.IsFinite(height) ||
            x < 0 || y < 0 || width <= 0 || height <= 0 ||
            x + width > 1 || y + height > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Crop must remain inside 0..1.");
        }

        X = x;
        Y = y;
        Width = width;
        Height = height;
    }
}

public sealed record SpanViewport(
    MonitorIdentity Monitor,
    DisplayViewport VirtualCanvas,
    DisplayViewport OutputViewport,
    NormalizedSourceRect SourceCrop);

public sealed class SpanLayout : IEquatable<SpanLayout>
{
    public SpanLayout(DisplayViewport virtualCanvas, IReadOnlyList<SpanViewport> viewports)
    {
        ArgumentNullException.ThrowIfNull(virtualCanvas);
        ArgumentNullException.ThrowIfNull(viewports);
        if (viewports.Count == 0 || viewports.Any(static viewport => viewport is null))
            throw new ArgumentException("At least one non-null span viewport is required.", nameof(viewports));

        VirtualCanvas = virtualCanvas;
        Viewports = new ReadOnlyCollection<SpanViewport>(viewports.ToArray());
    }

    public DisplayViewport VirtualCanvas { get; }
    public IReadOnlyList<SpanViewport> Viewports { get; }

    public bool Equals(SpanLayout? other) =>
        other is not null && VirtualCanvas == other.VirtualCanvas && Viewports.SequenceEqual(other.Viewports);

    public override bool Equals(object? obj) => obj is SpanLayout other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(VirtualCanvas);
        foreach (var viewport in Viewports) hash.Add(viewport);
        return hash.ToHashCode();
    }
}
