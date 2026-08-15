using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Native;

namespace VibeWallpaper.Engine.Monitors;

public enum DisplayTopologyUnavailableReason
{
    NoInteractiveDesktop,
}

public sealed class DisplayTopologyUnavailableException : InvalidOperationException
{
    public DisplayTopologyUnavailableException(DisplayTopologyUnavailableReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public DisplayTopologyUnavailableReason Reason { get; }
}

public sealed class DisplayTopologyCaptureException : InvalidOperationException
{
    public DisplayTopologyCaptureException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

public sealed class DisplayConfigTopologyService : IDisplayTopologyService
{
    private readonly IDisplayConfigPathSource _pathSource;
    private readonly IPhysicalMonitorSource _monitorSource;
    private readonly IEdidIdentitySource _edidSource;
    private readonly IInteractiveDesktopProbe _desktopProbe;
    private long _version;

    public DisplayConfigTopologyService()
        : this(
            NativeDisplayConfigPathSource.Instance,
            NativePhysicalMonitorSource.Instance,
            SetupApiEdidIdentitySource.Instance,
            NativeInteractiveDesktopProbe.Instance)
    {
    }

    internal DisplayConfigTopologyService(
        IDisplayConfigPathSource pathSource,
        IPhysicalMonitorSource monitorSource,
        IEdidIdentitySource edidSource,
        IInteractiveDesktopProbe desktopProbe)
    {
        ArgumentNullException.ThrowIfNull(pathSource);
        ArgumentNullException.ThrowIfNull(monitorSource);
        ArgumentNullException.ThrowIfNull(edidSource);
        ArgumentNullException.ThrowIfNull(desktopProbe);
        _pathSource = pathSource;
        _monitorSource = monitorSource;
        _edidSource = edidSource;
        _desktopProbe = desktopProbe;
    }

    public bool IsInteractiveDesktopAvailable => _desktopProbe.IsAvailable;

    public DisplayTopologySnapshot Capture()
    {
        if (!IsInteractiveDesktopAvailable)
        {
            throw new DisplayTopologyUnavailableException(
                DisplayTopologyUnavailableReason.NoInteractiveDesktop,
                "No interactive Windows input desktop is available.");
        }

        IReadOnlyList<DisplayConfigPath> paths;
        IReadOnlyList<NativeMonitor> monitors;
        try
        {
            paths = _pathSource.QueryActivePaths();
            monitors = _monitorSource.EnumeratePhysicalPixelMonitors();
        }
        catch (Exception exception) when (exception is Win32Exception or MonitorDpiException)
        {
            throw new DisplayTopologyCaptureException("The active Windows display topology could not be captured.", exception);
        }

        if (monitors.Count == 0)
        {
            throw new DisplayTopologyCaptureException("EnumDisplayMonitors returned no active monitors on an interactive desktop.");
        }

        var pathGroups = paths
            .GroupBy(path => (path.AdapterLuid, path.SourceId))
            .ToArray();
        var outputs = new List<DisplayTopologyOutput>(monitors.Count);
        for (var monitorIndex = 0; monitorIndex < monitors.Count; monitorIndex++)
        {
            var monitor = monitors[monitorIndex];
            var group = pathGroups.SingleOrDefault(candidate =>
                candidate.Any(path => string.Equals(path.GdiDeviceName, monitor.DeviceName, StringComparison.OrdinalIgnoreCase)));
            outputs.Add(CreateOutput(monitor, group?.ToArray() ?? [], monitorIndex));
        }

        var virtualDesktop = CalculateVirtualDesktop(outputs);
        return new DisplayTopologySnapshot(
            Interlocked.Increment(ref _version),
            virtualDesktop,
            outputs);
    }

    private DisplayTopologyOutput CreateOutput(
        NativeMonitor monitor,
        IReadOnlyList<DisplayConfigPath> paths,
        int fallbackIndex)
    {
        var orderedPaths = paths
            .OrderBy(path => path.AdapterLuid)
            .ThenBy(path => path.TargetId)
            .ThenBy(path => path.MonitorDevicePath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var friendlyNames = orderedPaths
            .Select(path => path.FriendlyName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var friendlyName = friendlyNames.Length == 0
            ? monitor.DeviceName
            : string.Join(" / ", friendlyNames);
        if (string.IsNullOrWhiteSpace(friendlyName))
        {
            friendlyName = $"Display {fallbackIndex + 1}";
        }

        MonitorIdentityEvidence[] evidence;
        if (orderedPaths.Length == 0)
        {
            evidence =
            [
                new(
                    0,
                    (uint)fallbackIndex,
                    (uint)fallbackIndex,
                    null,
                    monitor.DeviceName,
                    null,
                    null,
                    null,
                    null,
                    friendlyName,
                    monitor.Bounds),
            ];
        }
        else
        {
            evidence = orderedPaths.Select(path =>
            {
                var edid = _edidSource.Find(path.MonitorDevicePath, path.TargetInstanceId);
                return new MonitorIdentityEvidence(
                    path.AdapterLuid,
                    path.SourceId,
                    path.TargetId,
                    path.ConnectorInstance,
                    path.TargetInstanceId,
                    path.MonitorDevicePath,
                    edid?.Manufacturer ?? path.EdidManufacturer,
                    edid?.ProductCode ?? path.EdidProductCode,
                    edid?.SerialNumber,
                    path.FriendlyName ?? friendlyName,
                    monitor.Bounds);
            }).ToArray();
        }

        var identity = new MonitorIdentity($"display:{HashIdentity(BuildIdentityMaterial(monitor, orderedPaths))}");
        var cloneGroupKey = orderedPaths.Length == 0
            ? $"source:{HashIdentity(monitor.DeviceName)}"
            : $"source:{HashIdentity($"{orderedPaths[0].AdapterLuid:X16}:{orderedPaths[0].SourceId}")}";
        var orientation = orderedPaths.Length == 0
            ? OrientationFromBounds(monitor.Bounds)
            : OrientationFromDisplayConfig(orderedPaths[0].Rotation, monitor.Bounds);
        var descriptor = new MonitorDescriptor(
            identity,
            evidence[0],
            friendlyName,
            monitor.Bounds,
            monitor.WorkArea,
            monitor.Dpi,
            monitor.Dpi / 96d,
            orientation,
            monitor.IsPrimary);

        return new(descriptor, cloneGroupKey, evidence);
    }

    private static string BuildIdentityMaterial(NativeMonitor monitor, IReadOnlyList<DisplayConfigPath> paths)
    {
        var devicePaths = paths
            .Select(path => path.MonitorDevicePath)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => path!.Trim().ToUpperInvariant())
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (devicePaths.Length > 0)
        {
            return string.Join("|", devicePaths);
        }

        if (paths.Count > 0)
        {
            return string.Join("|", paths.Select(path =>
                $"{path.AdapterLuid:X16}:{path.TargetId}:{path.ConnectorInstance?.ToString() ?? "?"}"));
        }

        return monitor.DeviceName.ToUpperInvariant();
    }

    private static string HashIdentity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static DisplayOrientation OrientationFromDisplayConfig(uint rotation, DisplayViewport bounds) => rotation switch
    {
        1 => DisplayOrientation.Landscape,
        2 => DisplayOrientation.Portrait,
        3 => DisplayOrientation.LandscapeFlipped,
        4 => DisplayOrientation.PortraitFlipped,
        _ => OrientationFromBounds(bounds),
    };

    private static DisplayOrientation OrientationFromBounds(DisplayViewport bounds) =>
        bounds.Width >= bounds.Height ? DisplayOrientation.Landscape : DisplayOrientation.Portrait;

    private static DisplayViewport CalculateVirtualDesktop(IReadOnlyList<DisplayTopologyOutput> outputs)
    {
        var left = outputs.Min(output => output.Descriptor.Bounds.X);
        var top = outputs.Min(output => output.Descriptor.Bounds.Y);
        var right = outputs.Max(output => checked(output.Descriptor.Bounds.X + output.Descriptor.Bounds.Width));
        var bottom = outputs.Max(output => checked(output.Descriptor.Bounds.Y + output.Descriptor.Bounds.Height));
        return new(left, top, checked(right - left), checked(bottom - top));
    }
}
