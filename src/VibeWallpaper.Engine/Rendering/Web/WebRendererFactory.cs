using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Rendering.Web;

public sealed class WebRendererFactory : IRendererFactory
{
    private readonly Func<WallpaperDefinition, IWebControllerAdapter> _adapterFactory;

    public WebRendererFactory(Func<IWebControllerAdapter> adapterFactory)
        : this(_ => (adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory)))())
    {
    }

    public WebRendererFactory(Func<WallpaperDefinition, IWebControllerAdapter> adapterFactory) =>
        _adapterFactory = adapterFactory ?? throw new ArgumentNullException(nameof(adapterFactory));

    public IWallpaperRenderer Create(WallpaperDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Source is not WebSource) throw new ArgumentException("The web renderer requires a web source.", nameof(definition));
        return new WebRenderer(_adapterFactory(definition));
    }
}
