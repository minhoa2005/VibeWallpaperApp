using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Runtime;

public enum EffectiveWallpaperKind
{
    Assigned,
    SolidFallback,
}

public sealed record EffectiveWallpaperState(
    WallpaperId? AssignedWallpaper,
    EffectiveWallpaperKind EffectiveKind,
    WallpaperId? EffectiveWallpaper,
    string? FallbackReasonCode);
