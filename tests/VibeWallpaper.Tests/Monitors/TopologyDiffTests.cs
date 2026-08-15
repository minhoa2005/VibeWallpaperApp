using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Monitors;

namespace VibeWallpaper.Tests.Monitors;

public sealed class TopologyDiffTests
{
    [Fact]
    public void Compare_WhenOutputMoves_ReportsChangedWithoutPhysicalAddOrRemove()
    {
        var previous = Snapshot(Output("stable", Rect(0, 0, 1920, 1080)));
        var current = Snapshot(Output("stable", Rect(1920, 0, 1920, 1080)));

        var diff = TopologyDiff.Compare(previous, current);

        Assert.Empty(diff.Added);
        Assert.Empty(diff.Removed);
        var change = Assert.Single(diff.Changed);
        Assert.Equal(0, change.Previous.Descriptor.Bounds.X);
        Assert.Equal(1920, change.Current.Descriptor.Bounds.X);
    }

    [Fact]
    public void Compare_ReportsAddedRemovedAndChangedLogicalOutputs()
    {
        var previous = Snapshot(
            Output("removed", Rect(0, 0, 1280, 720)),
            Output("changed", Rect(1280, 0, 1920, 1080)),
            Output("same", Rect(3200, 0, 1920, 1080)));
        var current = Snapshot(
            Output("changed", Rect(1280, 0, 2560, 1440)),
            Output("same", Rect(3200, 0, 1920, 1080)),
            Output("added", Rect(5120, 0, 3840, 2160)));

        var diff = TopologyDiff.Compare(previous, current);

        Assert.Equal("added", Assert.Single(diff.Added).Descriptor.Identity.Key);
        Assert.Equal("removed", Assert.Single(diff.Removed).Descriptor.Identity.Key);
        Assert.Equal("changed", Assert.Single(diff.Changed).Current.Descriptor.Identity.Key);
    }

    [Fact]
    public void Reconcile_WhenPersistedReferenceIsUnresolved_KeepsItUnavailable()
    {
        var unavailable = TestReference("missing", "DISPLAY#MISSING");
        var unrelated = Output("unrelated", Rect(0, 0, 1920, 1080), "DISPLAY#OTHER");

        var result = TopologyDiff.Reconcile([unavailable], Snapshot(unrelated));

        var assignment = Assert.Single(result);
        Assert.Same(unavailable, assignment.Persisted);
        Assert.Null(assignment.Output);
        Assert.False(assignment.IsAvailable);
    }

    [Fact]
    public void Snapshot_CopiesCallerOwnedCollections()
    {
        var evidence = new List<MonitorIdentityEvidence>();
        var output = Output("stable", Rect(0, 0, 1920, 1080));
        evidence.Add(output.Descriptor.Evidence);
        var copiedOutput = new DisplayTopologyOutput(output.Descriptor, output.CloneGroupKey, evidence);
        var outputs = new List<DisplayTopologyOutput> { copiedOutput };

        var snapshot = new DisplayTopologySnapshot(1, Rect(0, 0, 1920, 1080), outputs);
        evidence.Clear();
        outputs.Clear();

        Assert.Single(snapshot.LogicalOutputs);
        Assert.Single(snapshot.LogicalOutputs[0].TargetEvidence);
    }

    [Fact]
    public void Diagnostics_RedactsRawDevicePathsWithoutDiscardingOtherIdentityEvidence()
    {
        var snapshot = Snapshot(Output("stable", Rect(0, 0, 1920, 1080), @"\\?\DISPLAY#ACM123#SERIAL#{GUID}"));

        var redacted = DisplayTopologyDiagnostics.CreateRedacted(snapshot);

        var evidence = Assert.Single(redacted.LogicalOutputs).Descriptor.Evidence;
        Assert.DoesNotContain("DISPLAY#ACM123", evidence.MonitorDevicePath, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("redacted:sha256:", evidence.MonitorDevicePath);
        Assert.Equal(snapshot.LogicalOutputs[0].Descriptor.Evidence.AdapterLuid, evidence.AdapterLuid);
        Assert.Equal(snapshot.LogicalOutputs[0].Descriptor.Evidence.TargetId, evidence.TargetId);
    }

    private static DisplayTopologySnapshot Snapshot(params DisplayTopologyOutput[] outputs) =>
        new(1, Rect(0, 0, 7680, 2160), outputs);

    private static DisplayTopologyOutput Output(string key, DisplayViewport bounds, string? path = null)
    {
        var evidence = Evidence(path, key, bounds);
        var descriptor = new MonitorDescriptor(
            new MonitorIdentity(key), evidence, key, bounds, bounds, 96, 1.0,
            DisplayOrientation.Landscape, false);
        return new DisplayTopologyOutput(descriptor, $"clone:{key}", [evidence]);
    }

    private static VibeWallpaper.Engine.Core.Persistence.PersistedMonitorReference TestReference(string key, string? path)
    {
        var bounds = Rect(0, 0, 1920, 1080);
        return new(new MonitorIdentity(key), Evidence(path, key, bounds));
    }

    private static MonitorIdentityEvidence Evidence(string? path, string name, DisplayViewport bounds) =>
        new(0, 1, 1, null, null, path, null, null, null, name, bounds);

    private static DisplayViewport Rect(int x, int y, int width, int height) => new(x, y, width, height);
}
