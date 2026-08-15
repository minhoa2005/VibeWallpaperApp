using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Engine.Import;

public interface IWallpaperImportPreparer
{
    Task<WallpaperLibraryItem> PrepareVideoAsync(
        string sourcePath,
        CancellationToken cancellationToken);

    Task<WallpaperLibraryItem> PrepareWebAsync(
        string sourceDirectory,
        CancellationToken cancellationToken);

    Task<WallpaperLibraryItem> RevalidateAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken);
}
