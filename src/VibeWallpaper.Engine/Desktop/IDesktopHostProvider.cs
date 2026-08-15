using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Desktop;

public sealed record DesktopHostResolution(
    nint ParentHwnd,
    string Strategy,
    bool IsDegraded,
    string? Diagnostic,
    nint ShellViewHwnd = 0,
    bool RequiresLayeredChildren = false);

public interface IDesktopHostProvider : IAsyncDisposable
{
    Task<IWallpaperHostWindow> CreateAsync(
        MonitorDescriptor monitor,
        CancellationToken cancellationToken);

    Task ReattachAllAsync(CancellationToken cancellationToken);
}

internal interface IDesktopHostResolver
{
    DesktopHostResolution Resolve();
}
