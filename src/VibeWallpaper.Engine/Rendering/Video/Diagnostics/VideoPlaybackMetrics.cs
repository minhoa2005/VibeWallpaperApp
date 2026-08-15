using System.Threading;

namespace VibeWallpaper.Engine.Rendering.Video.Diagnostics;

public sealed class VideoPlaybackMetrics
{
    private readonly string _rendererId;
    private readonly string _outputKey;
    private readonly string _backend;
    private long _presentedFrames;
    private long _droppedFrames;
    private long _repeatedFrames;
    private long _loopGeneration;
    private int _recoveryCount;
    private int _hardwareDecodeConfirmed;

    public VideoPlaybackMetrics(string rendererId, string outputKey, string backend = "libvlc")
    {
        if (string.IsNullOrWhiteSpace(rendererId))
        {
            throw new ArgumentException("A renderer identifier is required.", nameof(rendererId));
        }

        if (string.IsNullOrWhiteSpace(outputKey))
        {
            throw new ArgumentException("An output key is required.", nameof(outputKey));
        }

        if (string.IsNullOrWhiteSpace(backend))
        {
            throw new ArgumentException("A backend identifier is required.", nameof(backend));
        }

        _rendererId = rendererId.Trim();
        _outputKey = outputKey.Trim();
        _backend = backend.Trim();
    }

    public void RecordPresented() => Interlocked.Increment(ref _presentedFrames);

    public void RecordDropped() => Interlocked.Increment(ref _droppedFrames);

    public void RecordRepeated() => Interlocked.Increment(ref _repeatedFrames);

    public void RecordLoop(long generation) => Interlocked.Exchange(ref _loopGeneration, generation);

    public void RecordRecovery() => Interlocked.Increment(ref _recoveryCount);

    public void SetHardwareDecodeConfirmed(bool confirmed) =>
        Interlocked.Exchange(ref _hardwareDecodeConfirmed, confirmed ? 1 : 0);

    public VideoPlaybackMetricsSnapshot Snapshot() => new(
        _rendererId,
        _outputKey,
        _backend,
        Interlocked.Read(ref _presentedFrames),
        Interlocked.Read(ref _droppedFrames),
        Interlocked.Read(ref _repeatedFrames),
        Interlocked.Read(ref _loopGeneration),
        Volatile.Read(ref _recoveryCount),
        Volatile.Read(ref _hardwareDecodeConfirmed) == 1);
}

public sealed record VideoPlaybackMetricsSnapshot(
    string RendererId,
    string OutputKey,
    string Backend,
    long PresentedFrames,
    long DroppedFrames,
    long RepeatedFrames,
    long LoopGeneration,
    int RecoveryCount,
    bool HardwareDecodeConfirmed);
