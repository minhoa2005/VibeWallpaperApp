using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Rendering.Web;

public static class WebWallpaperOrigin
{
    public static Uri Create(WallpaperId wallpaper)
    {
        if (wallpaper.Value == Guid.Empty)
        {
            throw new ArgumentException("A wallpaper ID is required.", nameof(wallpaper));
        }

        return new Uri($"https://wallpaper-{wallpaper.Value:N}.vibe.local/", UriKind.Absolute);
    }
}
