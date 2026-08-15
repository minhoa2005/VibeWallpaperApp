using VibeWallpaper.Engine.Diagnostics;

namespace VibeWallpaper.Engine.Rendering.Video.Diagnostics;

public interface IVideoPlaybackDiagnostics
{
    void Record(VideoPlaybackEvent playbackEvent);
    void Record(VideoPlaybackMetricsSnapshot snapshot);
}

public sealed record VideoPlaybackEvent(
    string Operation,
    string RendererId,
    string OutputKey,
    string Backend,
    string? FailureCode,
    int RetryCount,
    long DurationMilliseconds);

public sealed class LogSinkVideoPlaybackDiagnostics : IVideoPlaybackDiagnostics
{
    private const string Component = "video-playback";

    public static IVideoPlaybackDiagnostics None { get; } = new NoOpVideoPlaybackDiagnostics();

    private readonly ILogSink _sink;

    public LogSinkVideoPlaybackDiagnostics(ILogSink sink) =>
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));

    public void Record(VideoPlaybackEvent playbackEvent)
    {
        ArgumentNullException.ThrowIfNull(playbackEvent);
        _ = ObserveAsync(_sink.WriteEventAsync(
            new DiagnosticEvent(
                "info",
                Component,
                playbackEvent.RendererId,
                playbackEvent.Operation,
                playbackEvent.DurationMilliseconds,
                string.IsNullOrWhiteSpace(playbackEvent.FailureCode) ? "none" : "video",
                playbackEvent.FailureCode,
                playbackEvent.RetryCount,
                null,
                null,
                RendererId: playbackEvent.RendererId,
                OutputKey: playbackEvent.OutputKey,
                Backend: playbackEvent.Backend)));
    }

    public void Record(VideoPlaybackMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _ = ObserveAsync(_sink.WriteEventAsync(
            new DiagnosticEvent(
                "info",
                Component,
                snapshot.RendererId,
                "metrics",
                0,
                "none",
                null,
                0,
                null,
                null,
                RendererId: snapshot.RendererId,
                OutputKey: snapshot.OutputKey,
                Backend: snapshot.Backend,
                PresentedFrames: snapshot.PresentedFrames,
                DroppedFrames: snapshot.DroppedFrames,
                RepeatedFrames: snapshot.RepeatedFrames,
                LoopGeneration: snapshot.LoopGeneration,
                RecoveryCount: snapshot.RecoveryCount,
                HardwareDecodeConfirmed: snapshot.HardwareDecodeConfirmed)));
    }

    private static async Task ObserveAsync(ValueTask writeTask)
    {
        try
        {
            await writeTask.ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Diagnostics remain best-effort and must not interrupt playback or shutdown.
        }
    }

    private sealed class NoOpVideoPlaybackDiagnostics : IVideoPlaybackDiagnostics
    {
        public void Record(VideoPlaybackEvent playbackEvent)
        {
        }

        public void Record(VideoPlaybackMetricsSnapshot snapshot)
        {
        }
    }
}
