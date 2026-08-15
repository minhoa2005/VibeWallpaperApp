using VibeWallpaper.Engine.Rendering.Video;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Rendering;

public sealed class VideoDriftSynchronizerTests
{
    [Fact]
    public async Task SampleAsync_UsesStrictHundredMillisecondThreshold()
    {
        await using var dispatcher = new InlineDispatcher();
        var time = new ManualTimeProvider();
        var clock = new LoopingPlaybackClock(time, TimeSpan.FromSeconds(10));
        clock.Start(TimeSpan.FromSeconds(1));
        var synchronizer = new VideoDriftSynchronizer(dispatcher, clock, time, TimeSpan.FromSeconds(1));
        var within = new FakePlayback("within", TimeSpan.FromMilliseconds(901));
        var outside = new FakePlayback("outside", TimeSpan.FromMilliseconds(899));

        await synchronizer.SampleAsync([within, outside], TestContext.Current.CancellationToken);

        Assert.Empty(within.Seeks);
        Assert.Equal(TimeSpan.FromSeconds(1), Assert.Single(outside.Seeks));
    }

    [Fact]
    public async Task SampleAsync_UsesShortestPathAcrossLoopBoundary()
    {
        await using var dispatcher = new InlineDispatcher();
        var time = new ManualTimeProvider();
        var clock = new LoopingPlaybackClock(time, TimeSpan.FromSeconds(10));
        clock.Start(TimeSpan.FromSeconds(9.98));
        var synchronizer = new VideoDriftSynchronizer(dispatcher, clock, time, TimeSpan.FromSeconds(1));
        var boundaryNeighbor = new FakePlayback("boundary", TimeSpan.FromSeconds(0.02));

        await synchronizer.SampleAsync([boundaryNeighbor], TestContext.Current.CancellationToken);

        Assert.Empty(boundaryNeighbor.Seeks);
    }

    [Fact]
    public async Task SampleAsync_RateLimitsRepeatedCorrectionUntilCooldownExpires()
    {
        await using var dispatcher = new InlineDispatcher();
        var time = new ManualTimeProvider();
        var clock = new LoopingPlaybackClock(time, TimeSpan.FromSeconds(10));
        clock.Start(TimeSpan.Zero);
        var synchronizer = new VideoDriftSynchronizer(dispatcher, clock, time, TimeSpan.FromSeconds(1));
        var player = new FakePlayback("output", TimeSpan.FromMilliseconds(101));

        await synchronizer.SampleAsync([player], TestContext.Current.CancellationToken);
        player.Position = TimeSpan.FromMilliseconds(101);
        time.Advance(TimeSpan.FromMilliseconds(999));
        await synchronizer.SampleAsync([player], TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMilliseconds(1));
        await synchronizer.SampleAsync([player], TestContext.Current.CancellationToken);

        Assert.Equal(2, player.Seeks.Count);
    }

    [Fact]
    public async Task Resume_ForcesExactlyOneImmediateCorrection()
    {
        await using var dispatcher = new InlineDispatcher();
        var time = new ManualTimeProvider();
        var clock = new LoopingPlaybackClock(time, TimeSpan.FromSeconds(10));
        clock.Start(TimeSpan.FromSeconds(2));
        var synchronizer = new VideoDriftSynchronizer(dispatcher, clock, time, TimeSpan.FromMinutes(1));
        var player = new FakePlayback("output", TimeSpan.FromSeconds(2));

        synchronizer.NotifyResumed(player.Id);
        await synchronizer.SampleAsync([player], TestContext.Current.CancellationToken);
        await synchronizer.SampleAsync([player], TestContext.Current.CancellationToken);

        Assert.Equal(TimeSpan.FromSeconds(2), Assert.Single(player.Seeks));
    }

    private sealed class FakePlayback(string id, TimeSpan position) : IVideoPlaybackEndpoint
    {
        public string Id { get; } = id;
        public TimeSpan Duration { get; } = TimeSpan.FromSeconds(10);
        public TimeSpan Position { get; set; } = position;
        public List<TimeSpan> Seeks { get; } = [];
        public void Seek(TimeSpan position) { Position = position; Seeks.Add(position); }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _timestamp;
        public void Advance(TimeSpan duration) => _timestamp += duration.Ticks;
    }

    private sealed class InlineDispatcher : IEngineDispatcher
    {
        public bool HasThreadAccess => true;
        public Task InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default) => action(cancellationToken).AsTask();
        public async Task<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default) => await action(cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
