using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Runtime;

public sealed record DisplayGroupOutputBinding(
    DisplayTopologyOutput Output,
    nint HostHwnd,
    OutputWallpaperSettings Settings);

public sealed class DisplayGroupPlan
{
    internal DisplayGroupPlan(
        DisplayGroupDefinition definition,
        AssignmentRequest request,
        IReadOnlyList<MonitorIdentity> disconnectedMembers,
        IReadOnlyList<SpanViewport> spanViewports)
    {
        Definition = definition;
        Request = request;
        DisconnectedMembers = new ReadOnlyCollection<MonitorIdentity>(disconnectedMembers.ToArray());
        SpanViewports = new ReadOnlyCollection<SpanViewport>(spanViewports.ToArray());
    }

    public DisplayGroupDefinition Definition { get; }
    public AssignmentRequest Request { get; }
    public IReadOnlyList<MonitorIdentity> DisconnectedMembers { get; }
    public IReadOnlyList<SpanViewport> SpanViewports { get; }
    public int RendererCount => Request.Targets.Count;
}

public static class DisplayGroupPlanner
{
    public static DisplayGroupPlan Plan(
        DisplayGroupDefinition definition,
        WallpaperDefinition wallpaper,
        DisplayTopologySnapshot topology,
        IReadOnlyList<DisplayGroupOutputBinding> bindings,
        bool topologyIsStable)
    {
        if (!TryPlan(definition, wallpaper, topology, bindings, topologyIsStable, out var plan))
            throw new InvalidOperationException("Display-group planning is deferred until topology reconciliation is stable.");
        return plan;
    }

    public static bool TryPlan(
        DisplayGroupDefinition definition,
        WallpaperDefinition wallpaper,
        DisplayTopologySnapshot topology,
        IReadOnlyList<DisplayGroupOutputBinding> bindings,
        bool topologyIsStable,
        out DisplayGroupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(bindings);
        if (definition.Wallpaper != wallpaper.Id)
            throw new ArgumentException("The group and wallpaper definition must match.", nameof(wallpaper));
        if (!topologyIsStable)
        {
            plan = null!;
            return false;
        }

        var members = definition.Members.Select(static member => member.Key).ToHashSet(StringComparer.Ordinal);
        var topologyByKey = topology.LogicalOutputs.ToDictionary(static output => output.Descriptor.Identity.Key, StringComparer.Ordinal);
        var disconnected = definition.Members.Where(member => !topologyByKey.ContainsKey(member.Key)).ToArray();

        var connectedMembers = topology.LogicalOutputs
            .Where(output => members.Contains(output.Descriptor.Identity.Key))
            .ToArray();
        var bindingsByClone = bindings
            .Where(binding => members.Contains(binding.Output.Descriptor.Identity.Key) &&
                topologyByKey.ContainsKey(binding.Output.Descriptor.Identity.Key))
            .GroupBy(static binding => binding.Output.CloneGroupKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var missing = connectedMembers
            .GroupBy(static output => output.CloneGroupKey, StringComparer.Ordinal)
            .Where(group => !bindingsByClone.ContainsKey(group.Key))
            .SelectMany(static group => group.Select(output => output.Descriptor.Identity.Key))
            .OrderBy(static key => key, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Connected group members are missing host bindings: {string.Join(", ", missing)}.",
                nameof(bindings));
        }

        // DisplayConfig may expose multiple physical targets for Windows Clone. App-level groups
        // operate on one representative logical output for each clone source.
        var connected = connectedMembers
            .GroupBy(static output => output.CloneGroupKey, StringComparer.Ordinal)
            .Select(group => bindingsByClone[group.Key])
            .ToArray();
        if (connected.Length == 0)
            throw new InvalidOperationException("The group has no connected logical outputs.");
        if (connected.Any(static binding => binding.HostHwnd == 0))
            throw new ArgumentException("Every connected logical output requires a host window.", nameof(bindings));

        var layout = definition.Mode == DisplayMode.Span
            ? SpanLayoutCalculator.Calculate(connected.Select(static binding => binding.Output.Descriptor).ToArray())
            : null;
        var canvas = layout?.VirtualCanvas ?? topology.VirtualDesktop;
        var cropByOutput = layout?.Viewports.ToDictionary(static viewport => viewport.Monitor.Key, StringComparer.Ordinal);
        var targets = connected.Select(binding =>
        {
            var target = new OutputAssignmentTarget(
                binding.Output.Descriptor.Identity,
                binding.HostHwnd,
                binding.Output.Descriptor.Bounds,
                binding.Settings);
            return cropByOutput is not null && cropByOutput.TryGetValue(target.Monitor.Key, out var span)
                ? target with { SourceCrop = span.SourceCrop }
                : target;
        }).ToArray();
        var request = new AssignmentRequest(wallpaper, definition.Mode, definition.GroupId, canvas, targets);
        plan = new DisplayGroupPlan(definition, request, disconnected, layout?.Viewports ?? []);
        return true;
    }
}
