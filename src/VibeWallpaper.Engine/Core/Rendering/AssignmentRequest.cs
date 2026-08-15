using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Core.Rendering;

public sealed record OutputWallpaperSettings(
    FitMode Fit,
    int TargetFps,
    int VolumePercent);

public sealed record OutputAssignmentTarget(
    MonitorIdentity Monitor,
    nint HostHwnd,
    DisplayViewport Viewport,
    OutputWallpaperSettings Settings)
{
    public NormalizedSourceRect? SourceCrop { get; init; }
}

public sealed record AssignmentRequest(
    WallpaperDefinition Wallpaper,
    DisplayMode Mode,
    DisplayGroupId? GroupId,
    DisplayViewport VirtualCanvas,
    IReadOnlyList<OutputAssignmentTarget> Targets);

public enum AssignmentOutcome
{
    Applied,
    Superseded,
}

public enum AssignmentDiagnosticCode
{
    RollbackFailed,
    HostUnavailable,
}

public sealed record AssignmentDiagnostic(
    MonitorIdentity? Output,
    AssignmentDiagnosticCode Code,
    string? NativeErrorCode);

public sealed record AssignmentResult(
    long Generation,
    AssignmentOutcome Outcome,
    IReadOnlyList<MonitorIdentity> AppliedOutputs,
    bool Persisted,
    IReadOnlyList<AssignmentDiagnostic> Diagnostics);

public sealed class WallpaperActivationException : Exception
{
    public WallpaperActivationException(
        string message,
        Exception innerException,
        IReadOnlyList<AssignmentDiagnostic>? diagnostics = null)
        : base(message, innerException) => Diagnostics = diagnostics ?? [];

    public IReadOnlyList<AssignmentDiagnostic> Diagnostics { get; }
}
