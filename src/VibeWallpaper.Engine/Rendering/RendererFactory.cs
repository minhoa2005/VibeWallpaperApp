using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Rendering;

public sealed class RendererCapabilityUnavailableException : Exception
{
    public RendererCapabilityUnavailableException(WallpaperKind kind)
        : base($"The {kind} renderer capability is unavailable.") => Kind = kind;

    public WallpaperKind Kind { get; }
}

public sealed class RendererFactory : IRendererFactory
{
    private readonly IRendererFactory _solid;
    private readonly IRendererFactory _video;
    private readonly IRendererFactory? _web;

    public RendererFactory(IRendererFactory solid, IRendererFactory video, IRendererFactory? web = null)
    {
        _solid = solid ?? throw new ArgumentNullException(nameof(solid));
        _video = video ?? throw new ArgumentNullException(nameof(video));
        _web = web;
    }

    public IWallpaperRenderer Create(WallpaperDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.Source.Kind switch
        {
            WallpaperKind.SolidColor => _solid.Create(definition),
            WallpaperKind.Video => _video.Create(definition),
            WallpaperKind.Web => _web?.Create(definition) ?? throw new RendererCapabilityUnavailableException(WallpaperKind.Web),
            _ => throw new ArgumentException("A defined wallpaper kind is required.", nameof(definition)),
        };
    }
}
