using VibeWallpaper.Engine.Activity;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Monitors;

namespace VibeWallpaper.Tests.Activity;

public sealed class ActivitySnapshotBuilderTests
{
    [Fact]
    public void Build_RecapturesEveryFactAndCoverageInsteadOfPatchingEventState()
    {
        var output = Monitor("DISPLAY-A", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040));
        var topology = new MutableTopology(output);
        var windows = new MutableWindows
        {
            Value = [Window(101, output.Bounds)],
        };
        var facts = new MutableFacts
        {
            Value = new ActivitySystemFacts(true, true, true, true, true, true),
        };
        var builder = new ActivitySnapshotBuilder(topology, windows, facts, new FixedWindowContext());

        var first = builder.Build();
        facts.Value = new ActivitySystemFacts(false, false, false, false, false, false);
        windows.Value = [];
        var second = builder.Build();

        Assert.True(first.SessionLocked);
        Assert.True(first.DisplayOff);
        Assert.True(first.SystemSleeping);
        Assert.True(first.RunningOnBattery);
        Assert.True(first.BatterySaverEnabled);
        Assert.True(first.RemoteDesktopSession);
        Assert.Contains(output.Identity, (IEnumerable<MonitorIdentity>)first.FullscreenCoveredOutputs);
        Assert.False(second.SessionLocked);
        Assert.False(second.DisplayOff);
        Assert.False(second.SystemSleeping);
        Assert.False(second.RunningOnBattery);
        Assert.False(second.BatterySaverEnabled);
        Assert.False(second.RemoteDesktopSession);
        Assert.Empty(second.FullscreenCoveredOutputs);
        Assert.Equal(2, topology.CaptureCount);
        Assert.Equal(2, windows.CaptureCount);
        Assert.Equal(2, facts.CaptureCount);
    }

    [Fact]
    public void Build_KeepsMaximizedWorkAreaSeparateFromFullscreen()
    {
        var output = Monitor("DISPLAY-A", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040));
        var builder = new ActivitySnapshotBuilder(
            new MutableTopology(output),
            new MutableWindows { Value = [Window(202, output.WorkArea)] },
            new MutableFacts(),
            new FixedWindowContext());

        var snapshot = builder.Build();

        Assert.Empty(snapshot.FullscreenCoveredOutputs);
        Assert.Contains(output.Identity, (IEnumerable<MonitorIdentity>)snapshot.MaximizedOutputs);
    }

    private static WindowSnapshot Window(nint hwnd, DisplayViewport bounds) =>
        new(hwnd, hwnd, 42, 0, bounds, true, false, false, false, false, false);

    private static MonitorDescriptor Monitor(string key, DisplayViewport bounds, DisplayViewport workArea)
    {
        var identity = new MonitorIdentity(key);
        var evidence = new MonitorIdentityEvidence(1, 1, 1, null, null, $"path-{key}", null, null, null, key, bounds);
        return new MonitorDescriptor(identity, evidence, key, bounds, workArea, 96, 1, DisplayOrientation.Landscape, true);
    }

    private sealed class MutableTopology(MonitorDescriptor monitor) : IDisplayTopologyService
    {
        public int CaptureCount { get; private set; }

        public DisplayTopologySnapshot Capture()
        {
            CaptureCount++;
            return new DisplayTopologySnapshot(
                CaptureCount,
                monitor.Bounds,
                [new DisplayTopologyOutput(monitor, monitor.Identity.Key, [monitor.Evidence])]);
        }
    }

    private sealed class MutableWindows : IWindowSnapshotProvider
    {
        public IReadOnlyList<WindowSnapshot> Value { get; set; } = [];
        public int CaptureCount { get; private set; }

        public IReadOnlyList<WindowSnapshot> Capture(nint desktopHostHwnd, IReadOnlySet<nint> applicationOwnedWindows)
        {
            CaptureCount++;
            return Value;
        }
    }

    private sealed class MutableFacts : IActivitySystemFactsProvider
    {
        public ActivitySystemFacts Value { get; set; } = ActivitySystemFacts.Empty;
        public int CaptureCount { get; private set; }

        public ActivitySystemFacts Capture()
        {
            CaptureCount++;
            return Value;
        }
    }

    private sealed class FixedWindowContext : IActivityWindowContextProvider
    {
        public ActivityWindowContext Capture() => new(999, new HashSet<nint>());
    }
}
