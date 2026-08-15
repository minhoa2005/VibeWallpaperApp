using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.App.Services;

public sealed record CommandResult(
    bool Succeeded,
    string? ErrorCode,
    string? UserMessage);

public sealed record ImportResult(
    CommandResult Result,
    WallpaperLibraryItem? ImportedItem);

public sealed record LibrarySnapshot
{
    public LibrarySnapshot(
        long version,
        IReadOnlyList<WallpaperLibraryItem> items,
        IReadOnlySet<VibeWallpaper.Engine.Core.Wallpapers.WallpaperId>? assignedIds = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        Version = version;
        Items = items;
        AssignedIds = assignedIds ?? new HashSet<VibeWallpaper.Engine.Core.Wallpapers.WallpaperId>();
    }

    public long Version { get; }
    public IReadOnlyList<WallpaperLibraryItem> Items { get; }
    public IReadOnlySet<VibeWallpaper.Engine.Core.Wallpapers.WallpaperId> AssignedIds { get; }
}

public enum UserNoticeSeverity
{
    Informational,
    Success,
    Warning,
    Error,
}

public sealed record UserNotice(
    bool IsOpen,
    UserNoticeSeverity Severity,
    string Message,
    string? ErrorCode)
{
    public static UserNotice Closed { get; } =
        new(false, UserNoticeSeverity.Informational, string.Empty, null);
}
