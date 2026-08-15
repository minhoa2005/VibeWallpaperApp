using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.App.Services;

public interface ILibraryController
{
    Task<LibrarySnapshot> GetLibraryAsync(CancellationToken cancellationToken);

    Task<ImportResult> ImportVideoAsync(
        string absolutePath,
        CancellationToken cancellationToken);

    Task<ImportResult> ImportWebAsync(
        string absoluteDirectory,
        CancellationToken cancellationToken);

    Task<CommandResult> RevalidateAsync(
        WallpaperId id,
        CancellationToken cancellationToken);

    Task<CommandResult> RemoveAsync(
        WallpaperId id,
        bool clearAssignments,
        CancellationToken cancellationToken);

    Task<CommandResult> SetNetworkPermissionAsync(
        WallpaperId id,
        bool enabled,
        CancellationToken cancellationToken);
}
