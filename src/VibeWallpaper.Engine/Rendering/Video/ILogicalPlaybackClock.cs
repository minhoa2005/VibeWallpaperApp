namespace VibeWallpaper.Engine.Rendering.Video;

public readonly record struct LoopingPlaybackPosition(TimeSpan MediaPosition, long LoopGeneration);

public interface ILoopingPlaybackClock
{
    TimeSpan Duration { get; }
    LoopingPlaybackPosition Position { get; }
    void Start(TimeSpan mediaPosition);
    void Pause();
    void Seek(TimeSpan mediaPosition);
}

public interface ILogicalPlaybackClock : ILoopingPlaybackClock;
