using System.Collections.Frozen;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Monitors;

namespace VibeWallpaper.Engine.Activity;

public sealed record ActivitySystemFacts(
    bool SessionLocked,
    bool DisplayOff,
    bool SystemSleeping,
    bool RunningOnBattery,
    bool BatterySaverEnabled,
    bool RemoteDesktopSession)
{
    public static ActivitySystemFacts Empty { get; } = new(false, false, false, false, false, false);
}

public sealed record ActivityWindowContext
{
    public nint DesktopHostHwnd { get; }
    public FrozenSet<nint> ApplicationOwnedWindows { get; }

    public ActivityWindowContext(nint desktopHostHwnd, IEnumerable<nint> applicationOwnedWindows)
    {
        if (desktopHostHwnd == 0) throw new ArgumentException("A Desktop host HWND is required.", nameof(desktopHostHwnd));
        ArgumentNullException.ThrowIfNull(applicationOwnedWindows);
        DesktopHostHwnd = desktopHostHwnd;
        ApplicationOwnedWindows = applicationOwnedWindows.ToFrozenSet();
    }
}

public interface IActivitySystemFactsProvider
{
    ActivitySystemFacts Capture();
}

public interface IActivityWindowContextProvider
{
    ActivityWindowContext Capture();
}

public interface IActivitySnapshotBuilder
{
    ActivitySnapshot Build();
}

public sealed class ActivitySnapshotBuilder : IActivitySnapshotBuilder
{
    private readonly IDisplayTopologyService _topology;
    private readonly IWindowSnapshotProvider _windows;
    private readonly IActivitySystemFactsProvider _facts;
    private readonly IActivityWindowContextProvider _windowContext;

    public ActivitySnapshotBuilder(
        IDisplayTopologyService topology,
        IWindowSnapshotProvider windows,
        IActivitySystemFactsProvider facts,
        IActivityWindowContextProvider windowContext)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(windowContext);
        _topology = topology;
        _windows = windows;
        _facts = facts;
        _windowContext = windowContext;
    }

    public ActivitySnapshot Build()
    {
        var topology = _topology.Capture();
        var context = _windowContext.Capture();
        var windows = _windows.Capture(context.DesktopHostHwnd, context.ApplicationOwnedWindows);
        var facts = _facts.Capture();
        var coverage = WindowCoverageClassifier.Classify(
            windows,
            topology.LogicalOutputs.Select(static output => output.Descriptor).ToArray());

        return new ActivitySnapshot(
            facts.SessionLocked,
            facts.DisplayOff,
            facts.SystemSleeping,
            facts.RunningOnBattery,
            facts.BatterySaverEnabled,
            facts.RemoteDesktopSession,
            coverage.Where(static item => item.Kind == CoverageKind.Fullscreen).Select(static item => item.Monitor),
            coverage.Where(static item => item.Kind == CoverageKind.MaximizedWorkArea).Select(static item => item.Monitor));
    }
}
