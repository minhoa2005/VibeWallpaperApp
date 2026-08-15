using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Core.Rendering;

public sealed record DisplayGroupDefinition
{
    public DisplayGroupId GroupId { get; }
    public DisplayMode Mode { get; }
    public WallpaperId Wallpaper { get; }
    public IReadOnlyList<MonitorIdentity> Members { get; }

    public DisplayGroupDefinition(
        DisplayGroupId groupId,
        DisplayMode mode,
        WallpaperId wallpaper,
        IReadOnlyList<MonitorIdentity> members)
    {
        if (groupId.Value == Guid.Empty) throw new ArgumentException("Group ID is required.", nameof(groupId));
        if (mode == DisplayMode.Independent || !Enum.IsDefined(mode))
            throw new ArgumentException("A display group must use Duplicate or Span mode.", nameof(mode));
        if (wallpaper.Value == Guid.Empty) throw new ArgumentException("Wallpaper is required.", nameof(wallpaper));
        ArgumentNullException.ThrowIfNull(members);
        if (members.Count == 0 || members.Any(static member => member is null))
            throw new ArgumentException("At least one non-null group member is required.", nameof(members));
        if (members.Select(static member => member.Key).Distinct(StringComparer.Ordinal).Count() != members.Count)
            throw new ArgumentException("Group members must be unique.", nameof(members));

        GroupId = groupId;
        Mode = mode;
        Wallpaper = wallpaper;
        Members = new ReadOnlyCollection<MonitorIdentity>(members.ToArray());
    }
}
