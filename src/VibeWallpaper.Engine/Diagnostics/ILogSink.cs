namespace VibeWallpaper.Engine.Diagnostics;

public interface ILogSink : IAsyncDisposable
{
    ValueTask WriteAsync(string level, string message, Exception? exception = null, CancellationToken cancellationToken = default);
    ValueTask WriteEventAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default);
}

public sealed record DiagnosticEvent(
    string Level,
    string Component,
    string? Identity,
    string Operation,
    long DurationMilliseconds,
    string FailureCategory,
    string? FailureCode,
    int RetryCount,
    string? FromState,
    string? ToState,
    string? RendererId = null,
    string? OutputKey = null,
    string? Backend = null,
    long? PresentedFrames = null,
    long? DroppedFrames = null,
    long? RepeatedFrames = null,
    long? LoopGeneration = null,
    int? RecoveryCount = null,
    bool? HardwareDecodeConfirmed = null);
