using VibeWallpaper.Engine.Rendering.Video.Diagnostics;

namespace VibeWallpaper.Tests.Rendering;

public sealed class VideoPlaybackMetricsTests
{
    [Fact]
    public void Snapshot_AggregatesFramesAndLifecycleCountsWithoutResettingIdentity()
    {
        var metrics = new VideoPlaybackMetrics("renderer-1", "output-1");

        metrics.RecordPresented();
        metrics.RecordDropped();
        metrics.RecordLoop(2);

        var snapshot = metrics.Snapshot();

        Assert.Equal("renderer-1", snapshot.RendererId);
        Assert.Equal("output-1", snapshot.OutputKey);
        Assert.Equal("libvlc", snapshot.Backend);
        Assert.Equal(1, snapshot.PresentedFrames);
        Assert.Equal(1, snapshot.DroppedFrames);
        Assert.Equal(2, snapshot.LoopGeneration);
    }

    [Fact]
    public void Snapshot_PreservesIdentityAcrossReadsAndTracksRecoveryAndHardwareDecode()
    {
        var metrics = new VideoPlaybackMetrics("renderer-2", "output-2");

        metrics.RecordRepeated();
        metrics.RecordRecovery();
        metrics.SetHardwareDecodeConfirmed(true);
        var first = metrics.Snapshot();

        metrics.RecordPresented();
        var second = metrics.Snapshot();

        Assert.Equal(first.RendererId, second.RendererId);
        Assert.Equal(first.OutputKey, second.OutputKey);
        Assert.Equal(first.Backend, second.Backend);
        Assert.Equal(1, first.RepeatedFrames);
        Assert.Equal(1, first.RecoveryCount);
        Assert.True(first.HardwareDecodeConfirmed);
        Assert.Equal(1, second.PresentedFrames);
    }
}
