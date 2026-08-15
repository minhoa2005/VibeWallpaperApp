using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Core.Activity;

public enum CoverageKind
{
    None,
    Partial,
    MaximizedWorkArea,
    Fullscreen,
}

public sealed record WindowCoverage(
    MonitorIdentity Monitor,
    nint Hwnd,
    CoverageKind Kind,
    double Fraction);
