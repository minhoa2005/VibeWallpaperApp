using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;

namespace VibeWallpaper.Engine.Rendering.Video;

public sealed class VideoRendererFactory : IRendererFactory
{
    private readonly IEngineDispatcher _dispatcher;
    private readonly ILibVlcRuntime _runtime;
    private readonly IVideoProbeService _probe;
    private readonly IVideoSurfaceWindowFactory _windows;
    private readonly VideoRendererOptions _options;
    private readonly IVideoPlaybackDiagnostics _diagnostics;

    public VideoRendererFactory(
        IEngineDispatcher dispatcher,
        ILibVlcRuntime runtime,
        IVideoProbeService probe,
        IVideoPlaybackDiagnostics? diagnostics = null,
        VideoRendererOptions? options = null)
        : this(dispatcher, runtime, probe, VideoSurfaceWindowFactory.Instance, diagnostics, options ?? VideoRendererOptions.Default)
    {
    }

    internal VideoRendererFactory(
        IEngineDispatcher dispatcher,
        ILibVlcRuntime runtime,
        IVideoProbeService probe,
        IVideoSurfaceWindowFactory windows,
        IVideoPlaybackDiagnostics? diagnostics,
        VideoRendererOptions options)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        _diagnostics = diagnostics ?? LogSinkVideoPlaybackDiagnostics.None;
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public IWallpaperRenderer Create(WallpaperDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Source.Kind != WallpaperKind.Video)
        {
            throw new ArgumentException("The video renderer factory accepts only video wallpapers.", nameof(definition));
        }

        return new VideoRenderer(_dispatcher, _runtime, _probe, _windows, _options, diagnostics: _diagnostics);
    }
}
