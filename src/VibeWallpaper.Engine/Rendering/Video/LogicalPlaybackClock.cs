namespace VibeWallpaper.Engine.Rendering.Video;

public sealed class LogicalPlaybackClock : ILogicalPlaybackClock
{
    private readonly LoopingPlaybackClock _inner;

    public LogicalPlaybackClock(TimeProvider? timeProvider = null) =>
        _inner = new LoopingPlaybackClock(timeProvider, TimeSpan.MaxValue);

    public TimeSpan Duration => _inner.Duration;

    public LoopingPlaybackPosition Position => _inner.Position;

    public void Start(TimeSpan mediaPosition) => _inner.Start(mediaPosition);

    public void Pause() => _inner.Pause();

    public void Seek(TimeSpan mediaPosition) => _inner.Seek(mediaPosition);
}
