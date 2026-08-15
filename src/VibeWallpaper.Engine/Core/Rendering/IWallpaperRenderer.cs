using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Core.Rendering;

public sealed record RendererContext
{
    /// <summary>A borrowed host window handle; renderers never destroy this handle.</summary>
    public nint HostHwnd { get; }

    public MonitorDescriptor Monitor { get; }

    public DisplayViewport VirtualCanvas { get; }

    public DisplayViewport Viewport { get; }

    public OutputWallpaperSettings Settings { get; }

    public NormalizedSourceRect SourceCrop { get; }

    public RendererContext(nint hostHwnd, MonitorDescriptor monitor, DisplayViewport virtualCanvas, DisplayViewport viewport)
        : this(
            hostHwnd,
            monitor,
            virtualCanvas,
            viewport,
            new OutputWallpaperSettings(FitMode.Cover, 30, 0))
    {
    }

    public RendererContext(
        nint hostHwnd,
        MonitorDescriptor monitor,
        DisplayViewport virtualCanvas,
        DisplayViewport viewport,
        OutputWallpaperSettings settings,
        NormalizedSourceRect? sourceCrop = null)
    {
        if (hostHwnd == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(hostHwnd));
        }

        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(virtualCanvas);
        ArgumentNullException.ThrowIfNull(viewport);
        ArgumentNullException.ThrowIfNull(settings);
        if (!Enum.IsDefined(settings.Fit))
        {
            throw new ArgumentException("A defined fit mode is required.", nameof(settings));
        }

        if (settings.TargetFps is < 1 or > 60)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }

        if (settings.VolumePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(settings));
        }

        HostHwnd = hostHwnd;
        Monitor = monitor;
        VirtualCanvas = virtualCanvas;
        Viewport = viewport;
        Settings = settings;
        SourceCrop = sourceCrop ?? new NormalizedSourceRect(0, 0, 1, 1);
    }
}

public interface IWallpaperRenderer : IAsyncDisposable
{
    RendererLifecycle Lifecycle { get; }

    PerformanceState PerformanceState { get; }

    RendererCapabilities Capabilities { get; }

    /// <summary>Uses a borrowed <see cref="RendererContext.HostHwnd"/> that the renderer must never destroy.</summary>
    Task InitializeAsync(RendererContext context, CancellationToken cancellationToken);

    Task LoadAsync(WallpaperSource source, CancellationToken cancellationToken);

    Task ActivateAsync(CancellationToken cancellationToken);

    Task ApplyPerformanceAsync(RendererPerformanceRequest request, CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}

public interface IRendererFactory
{
    IWallpaperRenderer Create(WallpaperDefinition definition);
}
