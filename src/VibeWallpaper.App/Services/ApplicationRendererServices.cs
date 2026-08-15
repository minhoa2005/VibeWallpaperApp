using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Diagnostics;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Rendering;
using VibeWallpaper.Engine.Rendering.Solid;
using VibeWallpaper.Engine.Rendering.Video;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;
using VibeWallpaper.Engine.Rendering.Web;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.App.Services;

public sealed class ApplicationRendererServices : IAsyncDisposable
{
    private readonly ILibVlcRuntime _libVlc;
    private bool _disposed;

    private ApplicationRendererServices(IRendererFactory factory, ILibVlcRuntime libVlc)
    {
        Factory = factory;
        _libVlc = libVlc;
    }

    public IRendererFactory Factory { get; }

    public static bool SupportsRenderer(WallpaperKind kind) =>
        kind is WallpaperKind.SolidColor or WallpaperKind.Video or WallpaperKind.Web;

    public static ApplicationRendererServices CreateDefault(
        IEngineDispatcher dispatcher,
        string webViewUserDataFolder,
        ILogSink? log = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        if (string.IsNullOrWhiteSpace(webViewUserDataFolder) || !Path.IsPathFullyQualified(webViewUserDataFolder))
            throw new ArgumentException("An absolute WebView2 user-data folder is required.", nameof(webViewUserDataFolder));
        var libVlc = new DeferredLibVlcRuntime(static () => new LibVlcRuntime());
        var diagnostics = log is null
            ? LogSinkVideoPlaybackDiagnostics.None
            : new LogSinkVideoPlaybackDiagnostics(log);
        var factory = new RendererFactory(
            new SolidColorRendererFactory(dispatcher),
            new VideoRendererFactory(dispatcher, libVlc, new VideoProbeService(), diagnostics),
            new WebRendererFactory(definition =>
                new WebView2ControllerAdapter(definition, webViewUserDataFolder)));
        return new ApplicationRendererServices(factory, libVlc);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _libVlc.DisposeAsync();
    }
}

internal sealed class DeferredLibVlcRuntime(Func<ILibVlcRuntime> factory) : ILibVlcRuntime
{
    private readonly Lazy<ILibVlcRuntime> _inner = new(
        factory ?? throw new ArgumentNullException(nameof(factory)),
        LazyThreadSafetyMode.ExecutionAndPublication);
    private bool _disposed;

    public bool HardwareDecodingRequested => true;
    public string Version => GetRuntime().Version;

    public ILibVlcPlayer CreatePlayer() => GetRuntime().CreatePlayer();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_inner.IsValueCreated) await _inner.Value.DisposeAsync();
    }

    private ILibVlcRuntime GetRuntime()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _inner.Value;
    }
}
