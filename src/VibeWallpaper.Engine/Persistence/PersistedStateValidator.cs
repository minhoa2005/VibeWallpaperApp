using System.Diagnostics.CodeAnalysis;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Persistence;

public static class StateValidationCodes
{
    public const string InvalidSchemaVersion = "state.invalid_schema_version";
    public const string DuplicateWallpaperId = "state.duplicate_wallpaper_id";
    public const string DuplicateAssignmentOutput = "state.duplicate_assignment_output";
    public const string DuplicateGroupId = "state.duplicate_group_id";
    public const string AssignmentWallpaperMissing = "state.assignment_wallpaper_missing";
    public const string GroupIndependent = "state.group_independent";
    public const string GroupMembersEmpty = "state.group_members_empty";
    public const string GroupMemberDuplicate = "state.group_member_duplicate";
    public const string GroupMemberMissingAssignment = "state.group_member_missing_assignment";
    public const string IndependentAssignmentHasGroup = "state.independent_assignment_has_group";
    public const string GroupedAssignmentMissingGroup = "state.grouped_assignment_missing_group";
    public const string AssignmentGroupMissing = "state.assignment_group_missing";
    public const string GroupMismatch = "state.group_mismatch";
    public const string FallbackWallpaperMissing = "state.fallback_wallpaper_missing";
    public const string FallbackWallpaperNotSolid = "state.fallback_wallpaper_not_solid";
    public const string AudioOwnerAssignmentMissing = "state.audio_owner_assignment_missing";
    public const string AudioOwnerNotVideo = "state.audio_owner_not_video";
    public const string AudioOwnerAudioDisabled = "state.audio_owner_audio_disabled";
    public const string InvalidMonitorEvidence = "state.invalid_monitor_evidence";
}

public sealed class PersistenceValidationException(string diagnosticCode)
    : Exception(diagnosticCode)
{
    public string DiagnosticCode { get; } = diagnosticCode;
}

public static class PersistedStateValidator
{
    public static PersistedState ValidateAndNormalize(PersistedState state, WallpaperId? fallbackWallpaper = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != 1) Fail(StateValidationCodes.InvalidSchemaVersion);

        EnsureUnique(
            state.Library.Select(static item => item.Definition.Id),
            StateValidationCodes.DuplicateWallpaperId);
        EnsureUnique(
            state.Assignments.Select(static assignment => assignment.Monitor.Identity.Key),
            StateValidationCodes.DuplicateAssignmentOutput,
            StringComparer.Ordinal);
        EnsureUnique(
            state.Groups.Select(static group => group.Id),
            StateValidationCodes.DuplicateGroupId);

        var library = state.Library.ToDictionary(static item => item.Definition.Id);
        var assignments = state.Assignments.ToDictionary(
            static assignment => assignment.Monitor.Identity.Key,
            StringComparer.Ordinal);
        var groups = state.Groups.ToDictionary(static group => group.Id);

        foreach (var assignment in state.Assignments)
        {
            var evidence = assignment.Monitor.Evidence;
            if (evidence is null || string.IsNullOrWhiteSpace(evidence.FriendlyName) || evidence.LastBounds is null)
                Fail(StateValidationCodes.InvalidMonitorEvidence);
            if (!library.ContainsKey(assignment.Wallpaper))
                Fail(StateValidationCodes.AssignmentWallpaperMissing);
        }

        foreach (var group in state.Groups)
        {
            if (group.Mode == DisplayMode.Independent) Fail(StateValidationCodes.GroupIndependent);
            if (group.Members.Count == 0) Fail(StateValidationCodes.GroupMembersEmpty);
            EnsureUnique(
                group.Members.Select(static member => member.Key),
                StateValidationCodes.GroupMemberDuplicate,
                StringComparer.Ordinal);

            foreach (var member in group.Members)
            {
                if (!assignments.TryGetValue(member.Key, out var assignment))
                    Fail(StateValidationCodes.GroupMemberMissingAssignment);
                if (assignment.GroupId != group.Id || assignment.Mode != group.Mode || assignment.Wallpaper != group.Wallpaper)
                    Fail(StateValidationCodes.GroupMismatch);
            }
        }

        foreach (var assignment in state.Assignments)
        {
            if (assignment.Mode == DisplayMode.Independent)
            {
                if (assignment.GroupId.HasValue) Fail(StateValidationCodes.IndependentAssignmentHasGroup);
                continue;
            }

            if (!assignment.GroupId.HasValue) Fail(StateValidationCodes.GroupedAssignmentMissingGroup);
            if (!groups.TryGetValue(assignment.GroupId.Value, out var group))
                Fail(StateValidationCodes.AssignmentGroupMissing);
            if (group.Mode != assignment.Mode || group.Wallpaper != assignment.Wallpaper ||
                !group.Members.Any(member => string.Equals(member.Key, assignment.Monitor.Identity.Key, StringComparison.Ordinal)))
                Fail(StateValidationCodes.GroupMismatch);
        }

        if (fallbackWallpaper.HasValue)
        {
            if (!library.TryGetValue(fallbackWallpaper.Value, out var fallback))
                Fail(StateValidationCodes.FallbackWallpaperMissing);
            if (fallback.Definition.Source is not SolidColorSource)
                Fail(StateValidationCodes.FallbackWallpaperNotSolid);
        }

        if (state.AudioOwner is not null)
        {
            if (!assignments.TryGetValue(state.AudioOwner.Key, out var ownerAssignment))
                Fail(StateValidationCodes.AudioOwnerAssignmentMissing);
            var ownerWallpaper = library[ownerAssignment.Wallpaper].Definition;
            if (ownerWallpaper.Source is not VideoSource) Fail(StateValidationCodes.AudioOwnerNotVideo);
            if (!ownerWallpaper.AudioEnabled) Fail(StateValidationCodes.AudioOwnerAudioDisabled);
        }

        var normalizedGroups = state.Groups
            .Select(static group =>
            {
                var sorted = group.Members.OrderBy(static member => member.Key, StringComparer.Ordinal).ToArray();
                return group.Members.SequenceEqual(sorted) ? group : new PersistedDisplayGroup(group.Id, group.Mode, group.Wallpaper, sorted);
            })
            .ToArray();

        return state.Groups.SequenceEqual(normalizedGroups)
            ? state
            : new PersistedState(state.SchemaVersion, state.Library, state.Assignments, normalizedGroups, state.AudioOwner);
    }

    private static void EnsureUnique<T>(IEnumerable<T> values, string code, IEqualityComparer<T>? comparer = null)
    {
        var seen = new HashSet<T>(comparer);
        foreach (var value in values)
        {
            if (!seen.Add(value)) Fail(code);
        }
    }

    [DoesNotReturn]
    private static void Fail(string code) => throw new PersistenceValidationException(code);
}
