using VibeWallpaper.Engine.Rendering.Video;

namespace VibeWallpaper.Tests.Rendering;

public sealed class LoopingPlaybackClockTests
{
    [Fact]
    public void Position_WrapsByDurationAndIncrementsGeneration()
    {
        var time = new ManualTimeProvider();
        var clock = new LoopingPlaybackClock(time, TimeSpan.FromSeconds(10));
        clock.Start(TimeSpan.FromSeconds(9));
        time.Advance(TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(1), clock.Position.MediaPosition);
        Assert.Equal(1, clock.Position.LoopGeneration);
    }

    [Fact]
    public void Distance_UsesShortestPathAcrossLoopBoundary()
    {
        var duration = TimeSpan.FromSeconds(10);

        Assert.Equal(
            TimeSpan.FromMilliseconds(40),
            LoopingPlaybackClock.Distance(
                TimeSpan.FromSeconds(9.98),
                TimeSpan.FromSeconds(0.02),
                duration));
    }

    [Fact]
    public void StartAndSeek_NormalizePositivePositionsBeyondDuration()
    {
        var time = new ManualTimeProvider();
        var clock = new LoopingPlaybackClock(time, TimeSpan.FromSeconds(10));

        clock.Start(TimeSpan.FromSeconds(21));

        Assert.Equal(TimeSpan.FromSeconds(1), clock.Position.MediaPosition);
        Assert.Equal(0, clock.Position.LoopGeneration);

        clock.Seek(TimeSpan.FromSeconds(34));

        Assert.Equal(TimeSpan.FromSeconds(4), clock.Position.MediaPosition);
        Assert.Equal(0, clock.Position.LoopGeneration);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }
}
