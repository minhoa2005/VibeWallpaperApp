using System.Collections.Frozen;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Core.Activity;

public sealed record ActivitySnapshot
{
    public bool SessionLocked { get; }
    public bool DisplayOff { get; }
    public bool SystemSleeping { get; }
    public bool RunningOnBattery { get; }
    public bool BatterySaverEnabled { get; }
    public bool RemoteDesktopSession { get; }
    public FrozenSet<MonitorIdentity> FullscreenCoveredOutputs { get; }
    public FrozenSet<MonitorIdentity> MaximizedOutputs { get; }

    public ActivitySnapshot(
        bool sessionLocked,
        bool displayOff,
        bool systemSleeping,
        bool runningOnBattery,
        bool batterySaverEnabled,
        bool remoteDesktopSession,
        IEnumerable<MonitorIdentity> fullscreenCoveredOutputs,
        IEnumerable<MonitorIdentity> maximizedOutputs)
    {
        SessionLocked = sessionLocked;
        DisplayOff = displayOff;
        SystemSleeping = systemSleeping;
        RunningOnBattery = runningOnBattery;
        BatterySaverEnabled = batterySaverEnabled;
        RemoteDesktopSession = remoteDesktopSession;
        FullscreenCoveredOutputs = FreezeOutputs(fullscreenCoveredOutputs, nameof(fullscreenCoveredOutputs));
        MaximizedOutputs = FreezeOutputs(maximizedOutputs, nameof(maximizedOutputs));
    }

    private static FrozenSet<MonitorIdentity> FreezeOutputs(IEnumerable<MonitorIdentity> outputs, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        var copy = new HashSet<MonitorIdentity>();
        foreach (var output in outputs)
        {
            if (output is null)
            {
                throw new ArgumentException("Output collections cannot contain null elements.", parameterName);
            }

            copy.Add(output);
        }

        return copy.ToFrozenSet();
    }
}
