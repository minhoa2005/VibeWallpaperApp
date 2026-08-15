namespace VibeWallpaper.Engine.Rendering.Video;

public sealed class LoopingPlaybackClock : ILogicalPlaybackClock
{
    private readonly TimeProvider _timeProvider;
    private readonly long _durationTicks;
    private readonly object _gate = new();
    private long _baseTotalTicks;
    private long _startedTimestamp;
    private bool _running;

    public LoopingPlaybackClock(TimeProvider? timeProvider, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        _timeProvider = timeProvider ?? TimeProvider.System;
        Duration = duration;
        _durationTicks = duration.Ticks;
    }

    public TimeSpan Duration { get; }

    public LoopingPlaybackPosition Position
    {
        get
        {
            lock (_gate)
            {
                return CreatePosition(CurrentTotalTicks());
            }
        }
    }

    public void Start(TimeSpan mediaPosition)
    {
        lock (_gate)
        {
            _baseTotalTicks = NormalizePlaybackTicks(mediaPosition.Ticks);
            _startedTimestamp = _timeProvider.GetTimestamp();
            _running = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }

            _baseTotalTicks = CurrentTotalTicks();
            _running = false;
        }
    }

    public void Seek(TimeSpan mediaPosition)
    {
        lock (_gate)
        {
            _baseTotalTicks = NormalizePlaybackTicks(mediaPosition.Ticks);
            if (_running)
            {
                _startedTimestamp = _timeProvider.GetTimestamp();
            }
        }
    }

    public static TimeSpan Distance(TimeSpan from, TimeSpan to, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(duration));
        }

        var durationTicks = duration.Ticks;
        var fromTicks = NormalizeTicks(from.Ticks, durationTicks);
        var toTicks = NormalizeTicks(to.Ticks, durationTicks);
        var difference = Math.Abs(fromTicks - toTicks);
        return TimeSpan.FromTicks(Math.Min(difference, durationTicks - difference));
    }

    private long CurrentTotalTicks() => _running
        ? checked(_baseTotalTicks + _timeProvider.GetElapsedTime(_startedTimestamp, _timeProvider.GetTimestamp()).Ticks)
        : _baseTotalTicks;

    private long NormalizePlaybackTicks(long totalTicks) => NormalizeTicks(totalTicks, _durationTicks);

    private LoopingPlaybackPosition CreatePosition(long totalTicks)
    {
        var generation = Math.DivRem(totalTicks, _durationTicks, out var mediaTicks);
        return new LoopingPlaybackPosition(TimeSpan.FromTicks(mediaTicks), generation);
    }

    private static long NormalizeTicks(long ticks, long durationTicks)
    {
        var normalized = ticks % durationTicks;
        return normalized < 0 ? normalized + durationTicks : normalized;
    }
}
