using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Native;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Engine.Rendering.Solid;

public sealed class SolidColorRendererFactory : IRendererFactory
{
    private readonly IEngineDispatcher _dispatcher;
    private readonly ISolidRendererWindowApi _windows;

    public SolidColorRendererFactory(IEngineDispatcher dispatcher)
        : this(dispatcher, NativeDesktopWindowApi.Instance)
    {
    }

    internal SolidColorRendererFactory(IEngineDispatcher dispatcher, ISolidRendererWindowApi windows)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(windows);
        _dispatcher = dispatcher;
        _windows = windows;
    }

    public IWallpaperRenderer Create(WallpaperDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Source.Kind != WallpaperKind.SolidColor)
        {
            throw new ArgumentException("The solid renderer factory accepts only solid-color wallpapers.", nameof(definition));
        }

        return new SolidColorRenderer(_dispatcher, _windows);
    }
}
