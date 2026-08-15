using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Runtime;

public sealed record LibraryStateSnapshot
{
    public LibraryStateSnapshot(
        long version,
        IReadOnlyList<WallpaperLibraryItem> items,
        IReadOnlySet<WallpaperId>? assignedIds = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Version = version;
        Items = items;
        AssignedIds = assignedIds ?? new HashSet<WallpaperId>();
    }

    public long Version { get; }
    public IReadOnlyList<WallpaperLibraryItem> Items { get; }
    public IReadOnlySet<WallpaperId> AssignedIds { get; }
}

public sealed class LibraryStateException : Exception
{
    public LibraryStateException(string code, string message)
        : base(message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public interface ILibraryStateAuthority
{
    LibraryStateSnapshot GetLibrarySnapshot();

    Task<LibraryStateSnapshot> AddLibraryItemAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken);

    Task<LibraryStateSnapshot> ReplaceLibraryItemAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken);

    Task<LibraryStateSnapshot> RemoveLibraryItemAsync(
        WallpaperId id,
        bool clearAssignments,
        CancellationToken cancellationToken);

    Task<LibraryStateSnapshot> SetWebNetworkPermissionAsync(
        WallpaperId id,
        bool enabled,
        CancellationToken cancellationToken);
}
