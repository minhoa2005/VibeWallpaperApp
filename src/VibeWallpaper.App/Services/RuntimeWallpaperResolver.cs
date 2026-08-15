using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.App.Services;

public static class RuntimeWallpaperResolver
{
    public static WallpaperDefinition? Find(EngineSnapshot snapshot, WallpaperId id)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.State.Library
            .FirstOrDefault(item => item.Definition.Id == id)
            ?.Definition;
    }
}
