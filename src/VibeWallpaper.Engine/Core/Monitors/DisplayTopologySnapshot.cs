using System.Collections.ObjectModel;

namespace VibeWallpaper.Engine.Core.Monitors;

public sealed record DisplayTopologyOutput
{
    public MonitorDescriptor Descriptor { get; }

    public string CloneGroupKey { get; }

    public IReadOnlyList<MonitorIdentityEvidence> TargetEvidence { get; }

    public DisplayTopologyOutput(
        MonitorDescriptor descriptor,
        string cloneGroupKey,
        IReadOnlyList<MonitorIdentityEvidence> targetEvidence)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentException.ThrowIfNullOrWhiteSpace(cloneGroupKey);
        ArgumentNullException.ThrowIfNull(targetEvidence);
        if (targetEvidence.Count == 0 || targetEvidence.Any(static evidence => evidence is null))
        {
            throw new ArgumentException("At least one non-null target identity evidence item is required.", nameof(targetEvidence));
        }

        Descriptor = descriptor;
        CloneGroupKey = cloneGroupKey.Trim();
        TargetEvidence = new ReadOnlyCollection<MonitorIdentityEvidence>(targetEvidence.ToArray());
    }
}

public sealed record DisplayTopologySnapshot
{
    public long Version { get; }

    public DisplayViewport VirtualDesktop { get; }

    public IReadOnlyList<DisplayTopologyOutput> LogicalOutputs { get; }

    public DisplayTopologySnapshot(
        long version,
        DisplayViewport virtualDesktop,
        IReadOnlyList<DisplayTopologyOutput> logicalOutputs)
    {
        if (version < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version));
        }

        ArgumentNullException.ThrowIfNull(virtualDesktop);
        ArgumentNullException.ThrowIfNull(logicalOutputs);
        if (logicalOutputs.Any(static output => output is null))
        {
            throw new ArgumentException("Logical outputs cannot contain null.", nameof(logicalOutputs));
        }

        Version = version;
        VirtualDesktop = virtualDesktop;
        LogicalOutputs = new ReadOnlyCollection<DisplayTopologyOutput>(logicalOutputs.ToArray());
    }
}
