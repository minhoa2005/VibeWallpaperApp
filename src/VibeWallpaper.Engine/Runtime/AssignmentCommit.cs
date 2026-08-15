using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Runtime;

public sealed class AssignmentCommit
{
    private AssignmentCommit(PersistedState previous, PersistedState next)
    {
        Previous = previous;
        Next = next;
    }

    public PersistedState Previous { get; }
    public PersistedState Next { get; }

    public static AssignmentCommit Create(PersistedState current, AssignmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(request);

        var targetKeys = request.Targets.Select(static target => target.Monitor.Key).ToHashSet(StringComparer.Ordinal);
        var replacedGroupIds = current.Groups
            .Where(group =>
                (request.GroupId.HasValue && group.Id == request.GroupId.Value) ||
                group.Members.Any(member => targetKeys.Contains(member.Key)))
            .Select(static group => group.Id)
            .ToHashSet();
        var assignments = current.Assignments
            .Where(assignment => !targetKeys.Contains(assignment.Monitor.Identity.Key))
            .Select(assignment =>
            {
                if (!assignment.GroupId.HasValue || !replacedGroupIds.Contains(assignment.GroupId.Value))
                {
                    return assignment;
                }

                if (request.GroupId.HasValue && assignment.GroupId.Value == request.GroupId.Value)
                {
                    return null;
                }

                return new WallpaperAssignment(
                    assignment.Monitor,
                    assignment.Wallpaper,
                    DisplayMode.Independent,
                    assignment.Fit,
                    assignment.TargetFps,
                    assignment.VolumePercent,
                    null);
            })
            .Where(static assignment => assignment is not null)
            .Cast<WallpaperAssignment>()
            .ToList();
        assignments.AddRange(request.Targets.Select(target => CreateAssignment(request, target)));

        var groups = current.Groups
            .Where(group => !replacedGroupIds.Contains(group.Id))
            .ToList();
        if (request.GroupId is { } groupId)
        {
            groups.Add(new PersistedDisplayGroup(
                groupId,
                request.Mode,
                request.Wallpaper.Id,
                request.Targets.Select(static target => target.Monitor).ToArray()));
        }

        var library = current.Library.ToList();
        if (!library.Any(item => item.Definition.Id == request.Wallpaper.Id))
        {
            library.Add(new WallpaperLibraryItem(
                request.Wallpaper,
                null,
                null,
                new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UtcNow)));
        }

        var next = new PersistedState(
            current.SchemaVersion,
            library,
            assignments,
            groups,
            ResolveAudioOwner(current, request, assignments, replacedGroupIds));
        return new AssignmentCommit(current, next);
    }

    private static MonitorIdentity? ResolveAudioOwner(
        PersistedState current,
        AssignmentRequest request,
        IReadOnlyList<WallpaperAssignment> assignments,
        IReadOnlySet<DisplayGroupId> replacedGroupIds)
    {
        if (current.AudioOwner is not { } owner) return null;
        var previousOwner = current.Assignments.FirstOrDefault(assignment =>
            string.Equals(assignment.Monitor.Identity.Key, owner.Key, StringComparison.Ordinal));
        if (previousOwner?.GroupId is { } previousGroup && replacedGroupIds.Contains(previousGroup)) return null;
        var ownerAssignment = assignments.FirstOrDefault(assignment =>
            string.Equals(assignment.Monitor.Identity.Key, owner.Key, StringComparison.Ordinal));
        if (ownerAssignment is null) return null;

        var definition = ownerAssignment.Wallpaper == request.Wallpaper.Id
            ? request.Wallpaper
            : current.Library.FirstOrDefault(item => item.Definition.Id == ownerAssignment.Wallpaper)?.Definition;
        return definition is { Source: VideoSource, AudioEnabled: true } ? owner : null;
    }

    internal static MonitorDescriptor CreateDescriptor(OutputAssignmentTarget target)
    {
        var evidence = CreateEvidence(target);
        return new MonitorDescriptor(
            target.Monitor,
            evidence,
            target.Monitor.Key,
            target.Viewport,
            target.Viewport,
            96,
            1.0,
            DisplayOrientation.Landscape,
            false);
    }

    private static WallpaperAssignment CreateAssignment(AssignmentRequest request, OutputAssignmentTarget target) =>
        new(
            new PersistedMonitorReference(target.Monitor, CreateEvidence(target)),
            request.Wallpaper.Id,
            request.Mode,
            target.Settings.Fit,
            target.Settings.TargetFps,
            target.Settings.VolumePercent,
            request.GroupId);

    private static MonitorIdentityEvidence CreateEvidence(OutputAssignmentTarget target) =>
        new(0, 0, 0, null, null, null, null, null, null, target.Monitor.Key, target.Viewport);
}
