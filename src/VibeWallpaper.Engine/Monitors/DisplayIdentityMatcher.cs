using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Engine.Monitors;

public enum DisplayIdentityConfidence
{
    PreviousTopologySimilarity,
    CompatibleTarget,
    UniqueEdid,
    ExactDevicePath,
}

public enum DisplayIdentityEvidenceKind
{
    FriendlyNameAndBounds,
    AdapterTargetAndConnector,
    EdidManufacturerProductSerial,
    MonitorDevicePath,
}

public sealed record DisplayIdentityMatch(
    DisplayTopologyOutput Output,
    DisplayIdentityConfidence Confidence,
    DisplayIdentityEvidenceKind Evidence);

/// <summary>
/// Performs best-effort monitor reconciliation. Windows monitor identifiers are not
/// guaranteed to be permanent, so matching reports its evidence and confidence and
/// deliberately returns unresolved when the strongest available evidence is ambiguous.
/// </summary>
public static class DisplayIdentityMatcher
{
    public static DisplayTopologyOutput? Match(
        PersistedMonitorReference persisted,
        IReadOnlyList<DisplayTopologyOutput> candidates) =>
        MatchDetailed(persisted, candidates)?.Output;

    public static DisplayIdentityMatch? MatchDetailed(
        PersistedMonitorReference persisted,
        IReadOnlyList<DisplayTopologyOutput> candidates)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Any(static candidate => candidate is null))
        {
            throw new ArgumentException("Candidates cannot contain null.", nameof(candidates));
        }

        var evidence = persisted.Evidence;

        if (!string.IsNullOrWhiteSpace(evidence.MonitorDevicePath))
        {
            var pathMatches = UniqueMatches(candidates, candidate =>
                candidate.TargetEvidence.Any(target => SameText(target.MonitorDevicePath, evidence.MonitorDevicePath)));
            if (pathMatches.IsAmbiguous)
            {
                return null;
            }

            if (pathMatches.Output is not null)
            {
                return new(pathMatches.Output, DisplayIdentityConfidence.ExactDevicePath, DisplayIdentityEvidenceKind.MonitorDevicePath);
            }
        }

        if (HasCompleteEdid(evidence))
        {
            var edidMatches = UniqueMatches(candidates, candidate =>
                candidate.TargetEvidence.Any(target => SameEdid(target, evidence)));
            if (edidMatches.IsAmbiguous)
            {
                return null;
            }

            if (edidMatches.Output is not null)
            {
                return new(edidMatches.Output, DisplayIdentityConfidence.UniqueEdid, DisplayIdentityEvidenceKind.EdidManufacturerProductSerial);
            }
        }

        if (evidence.ConnectorInstance.HasValue)
        {
            var targetMatches = UniqueMatches(candidates, candidate =>
                candidate.TargetEvidence.Any(target => SameTargetAndConnector(target, evidence)));
            if (targetMatches.IsAmbiguous)
            {
                return null;
            }

            if (targetMatches.Output is not null)
            {
                return new(targetMatches.Output, DisplayIdentityConfidence.CompatibleTarget, DisplayIdentityEvidenceKind.AdapterTargetAndConnector);
            }
        }

        var similarityMatch = FindUniqueBestSimilarity(evidence, candidates);
        return similarityMatch is not null
            ? new(similarityMatch, DisplayIdentityConfidence.PreviousTopologySimilarity, DisplayIdentityEvidenceKind.FriendlyNameAndBounds)
            : null;
    }

    private static (DisplayTopologyOutput? Output, bool IsAmbiguous) UniqueMatches(
        IReadOnlyList<DisplayTopologyOutput> candidates,
        Func<DisplayTopologyOutput, bool> predicate)
    {
        DisplayTopologyOutput? match = null;
        foreach (var candidate in candidates)
        {
            if (!predicate(candidate))
            {
                continue;
            }

            if (match is not null)
            {
                return (null, true);
            }

            match = candidate;
        }

        return (match, false);
    }

    private static bool HasCompleteEdid(MonitorIdentityEvidence evidence) =>
        !string.IsNullOrWhiteSpace(evidence.EdidManufacturer) &&
        evidence.EdidProductCode.HasValue &&
        evidence.EdidSerialNumber.HasValue;

    private static bool SameEdid(MonitorIdentityEvidence current, MonitorIdentityEvidence persisted) =>
        HasCompleteEdid(current) &&
        SameText(current.EdidManufacturer, persisted.EdidManufacturer) &&
        current.EdidProductCode == persisted.EdidProductCode &&
        current.EdidSerialNumber == persisted.EdidSerialNumber;

    private static bool SameTargetAndConnector(MonitorIdentityEvidence current, MonitorIdentityEvidence persisted) =>
        current.AdapterLuid == persisted.AdapterLuid &&
        current.TargetId == persisted.TargetId &&
        current.ConnectorInstance == persisted.ConnectorInstance;

    private static DisplayTopologyOutput? FindUniqueBestSimilarity(
        MonitorIdentityEvidence persisted,
        IReadOnlyList<DisplayTopologyOutput> candidates)
    {
        DisplayTopologyOutput? best = null;
        var bestScore = 0;
        var isAmbiguous = false;
        foreach (var candidate in candidates)
        {
            if (HasConflictingDevicePath(persisted, candidate))
            {
                continue;
            }

            var current = candidate.Descriptor;
            var score = 0;
            if (SameText(persisted.FriendlyName, current.FriendlyName))
            {
                score += 3;
            }

            if (persisted.LastBounds == current.Bounds)
            {
                score += 3;
            }
            else if (persisted.LastBounds.Width == current.Bounds.Width &&
                     persisted.LastBounds.Height == current.Bounds.Height)
            {
                score++;
            }

            if (score > bestScore)
            {
                best = candidate;
                bestScore = score;
                isAmbiguous = false;
            }
            else if (score > 0 && score == bestScore)
            {
                isAmbiguous = true;
            }
        }

        return bestScore > 0 && !isAmbiguous ? best : null;
    }

    private static bool HasConflictingDevicePath(
        MonitorIdentityEvidence persisted,
        DisplayTopologyOutput candidate)
    {
        if (string.IsNullOrWhiteSpace(persisted.MonitorDevicePath))
        {
            return false;
        }

        var candidatePaths = candidate.TargetEvidence
            .Select(static evidence => evidence.MonitorDevicePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .ToArray();
        return candidatePaths.Length > 0 &&
               candidatePaths.All(path => !SameText(path, persisted.MonitorDevicePath));
    }

    private static bool SameText(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);
}
