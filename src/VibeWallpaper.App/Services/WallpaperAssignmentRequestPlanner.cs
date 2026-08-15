using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.App.Services;

public static class WallpaperAssignmentRequestPlanner
{
    public static AssignmentRequest Plan(
        WallpaperDefinition wallpaper,
        DisplayMode mode,
        IReadOnlyList<MonitorIdentity> selectedOutputs,
        DisplayTopologySnapshot topology,
        IReadOnlyDictionary<string, nint> hostHandles,
        DisplayGroupId? existingGroupId = null,
        IReadOnlyDictionary<string, OutputWallpaperSettings>? outputSettings = null)
    {
        ArgumentNullException.ThrowIfNull(wallpaper);
        ArgumentNullException.ThrowIfNull(selectedOutputs);
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(hostHandles);
        if (!Enum.IsDefined(mode)) throw new ArgumentException("A defined display mode is required.", nameof(mode));

        var selected = selectedOutputs
            .GroupBy(static output => output.Key, StringComparer.Ordinal)
            .Select(static group => group.First())
            .ToArray();
        if (selected.Length == 0)
            throw new ArgumentException("At least one output must be selected.", nameof(selectedOutputs));
        if (mode == DisplayMode.Independent && selected.Length != 1)
            throw new ArgumentException("Independent mode requires exactly one output.", nameof(selectedOutputs));
        if (mode == DisplayMode.Independent && existingGroupId is not null)
            throw new ArgumentException("Independent mode cannot use a display group.", nameof(existingGroupId));
        if (mode is DisplayMode.Duplicate or DisplayMode.Span && selected.Length < 2)
            throw new ArgumentException("Duplicate and Span modes require at least two outputs.", nameof(selectedOutputs));

        var topologyByKey = topology.LogicalOutputs.ToDictionary(
            static output => output.Descriptor.Identity.Key,
            StringComparer.Ordinal);
        var defaultSettings = new OutputWallpaperSettings(
            wallpaper.Fit,
            wallpaper.TargetFps,
            wallpaper.AudioEnabled ? wallpaper.VolumePercent : 0);
        var bindings = new List<DisplayGroupOutputBinding>(selected.Length);
        foreach (var output in selected)
        {
            if (!topologyByKey.TryGetValue(output.Key, out var topologyOutput))
                throw new ArgumentException($"Output '{output.Key}' is not connected.", nameof(selectedOutputs));
            if (!hostHandles.TryGetValue(output.Key, out var host) || host == 0)
                throw new ArgumentException($"Output '{output.Key}' has no wallpaper host.", nameof(hostHandles));
            var settings = outputSettings is not null && outputSettings.TryGetValue(output.Key, out var configured)
                ? configured
                : defaultSettings;
            bindings.Add(new DisplayGroupOutputBinding(topologyOutput, host, settings));
        }

        if (mode == DisplayMode.Independent)
        {
            var binding = bindings[0];
            return new AssignmentRequest(
                wallpaper,
                mode,
                null,
                topology.VirtualDesktop,
                [new OutputAssignmentTarget(
                    binding.Output.Descriptor.Identity,
                    binding.HostHwnd,
                    binding.Output.Descriptor.Bounds,
                    binding.Settings)]);
        }

        var definition = new DisplayGroupDefinition(
            existingGroupId ?? DisplayGroupId.New(), mode, wallpaper.Id, selected);
        return DisplayGroupPlanner.Plan(
            definition, wallpaper, topology, bindings, topologyIsStable: true).Request;
    }
}
