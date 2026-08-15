using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Engine.Monitors;

public sealed record DisplayTopologyChange(
    DisplayTopologyOutput Previous,
    DisplayTopologyOutput Current);

public sealed record ReconciledMonitorAssignment(
    PersistedMonitorReference Persisted,
    DisplayTopologyOutput? Output)
{
    public bool IsAvailable => Output is not null;
}

public sealed record TopologyDiff
{
    public IReadOnlyList<DisplayTopologyOutput> Added { get; }

    public IReadOnlyList<DisplayTopologyOutput> Removed { get; }

    public IReadOnlyList<DisplayTopologyChange> Changed { get; }

    private TopologyDiff(
        IReadOnlyList<DisplayTopologyOutput> added,
        IReadOnlyList<DisplayTopologyOutput> removed,
        IReadOnlyList<DisplayTopologyChange> changed)
    {
        Added = new ReadOnlyCollection<DisplayTopologyOutput>(added.ToArray());
        Removed = new ReadOnlyCollection<DisplayTopologyOutput>(removed.ToArray());
        Changed = new ReadOnlyCollection<DisplayTopologyChange>(changed.ToArray());
    }

    public static TopologyDiff Compare(DisplayTopologySnapshot previous, DisplayTopologySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(previous);
        ArgumentNullException.ThrowIfNull(current);

        var previousByIdentity = RequireUniqueIdentities(previous.LogicalOutputs, nameof(previous));
        var currentByIdentity = RequireUniqueIdentities(current.LogicalOutputs, nameof(current));

        var added = current.LogicalOutputs
            .Where(output => !previousByIdentity.ContainsKey(output.Descriptor.Identity.Key))
            .ToArray();
        var removed = previous.LogicalOutputs
            .Where(output => !currentByIdentity.ContainsKey(output.Descriptor.Identity.Key))
            .ToArray();
        var changed = current.LogicalOutputs
            .Where(output => previousByIdentity.ContainsKey(output.Descriptor.Identity.Key))
            .Select(output => new DisplayTopologyChange(previousByIdentity[output.Descriptor.Identity.Key], output))
            .Where(static change => HasChanged(change.Previous, change.Current))
            .ToArray();

        return new(added, removed, changed);
    }

    public static IReadOnlyList<ReconciledMonitorAssignment> Reconcile(
        IReadOnlyList<PersistedMonitorReference> persisted,
        DisplayTopologySnapshot current)
    {
        ArgumentNullException.ThrowIfNull(persisted);
        ArgumentNullException.ThrowIfNull(current);

        var available = current.LogicalOutputs.ToList();
        var result = new List<ReconciledMonitorAssignment>(persisted.Count);
        foreach (var reference in persisted)
        {
            ArgumentNullException.ThrowIfNull(reference);
            var match = DisplayIdentityMatcher.Match(reference, available);
            result.Add(new(reference, match));
            if (match is not null)
            {
                available.Remove(match);
            }
        }

        return new ReadOnlyCollection<ReconciledMonitorAssignment>(result);
    }

    private static Dictionary<string, DisplayTopologyOutput> RequireUniqueIdentities(
        IReadOnlyList<DisplayTopologyOutput> outputs,
        string parameterName)
    {
        try
        {
            return outputs.ToDictionary(output => output.Descriptor.Identity.Key, StringComparer.Ordinal);
        }
        catch (ArgumentException exception)
        {
            throw new ArgumentException("Topology logical identities must be unique.", parameterName, exception);
        }
    }

    private static bool HasChanged(DisplayTopologyOutput previous, DisplayTopologyOutput current) =>
        previous.CloneGroupKey != current.CloneGroupKey ||
        previous.Descriptor.FriendlyName != current.Descriptor.FriendlyName ||
        previous.Descriptor.Bounds != current.Descriptor.Bounds ||
        previous.Descriptor.WorkArea != current.Descriptor.WorkArea ||
        previous.Descriptor.Dpi != current.Descriptor.Dpi ||
        previous.Descriptor.DpiScale != current.Descriptor.DpiScale ||
        previous.Descriptor.Orientation != current.Descriptor.Orientation ||
        previous.Descriptor.IsPrimary != current.Descriptor.IsPrimary ||
        !previous.TargetEvidence.SequenceEqual(current.TargetEvidence);
}
