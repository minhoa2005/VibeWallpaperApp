using System.Text;
using System.Text.Json;

namespace VibeWallpaper.Engine.Diagnostics;

public sealed class RollingFileLogSink : ILogSink
{
    private readonly string _path;
    private readonly long _maximumBytes;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public RollingFileLogSink(string directory, long maximumBytes = 2 * 1024 * 1024)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (maximumBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        Directory.CreateDirectory(directory);
        _path = Path.Combine(Path.GetFullPath(directory), "vibe-wallpaper.jsonl");
        _maximumBytes = maximumBytes;
    }

    public async ValueTask WriteAsync(string level, string message, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(level);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RotateIfNeeded();

            var line = $"{DateTimeOffset.UtcNow:O} [{level.Trim().ToUpperInvariant()}] {message.Trim()}" +
                (exception is null ? string.Empty : $" | {exception}") + Environment.NewLine;
            await File.AppendAllTextAsync(_path, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask WriteEventAsync(DiagnosticEvent diagnosticEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(diagnosticEvent);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Level);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Component);
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticEvent.Operation);
        if (diagnosticEvent.DurationMilliseconds < 0 || diagnosticEvent.RetryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(diagnosticEvent));
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            RotateIfNeeded();
            var line = JsonSerializer.Serialize(new
            {
                timestamp = DateTimeOffset.UtcNow,
                level = diagnosticEvent.Level.Trim().ToUpperInvariant(),
                component = diagnosticEvent.Component.Trim(),
                identity = diagnosticEvent.Identity,
                operation = diagnosticEvent.Operation.Trim(),
                durationMs = diagnosticEvent.DurationMilliseconds,
                failureCategory = diagnosticEvent.FailureCategory,
                failureCode = diagnosticEvent.FailureCode,
                retryCount = diagnosticEvent.RetryCount,
                fromState = diagnosticEvent.FromState,
                toState = diagnosticEvent.ToState,
                rendererId = diagnosticEvent.RendererId,
                outputKey = diagnosticEvent.OutputKey,
                backend = diagnosticEvent.Backend,
                presentedFrames = diagnosticEvent.PresentedFrames,
                droppedFrames = diagnosticEvent.DroppedFrames,
                repeatedFrames = diagnosticEvent.RepeatedFrames,
                loopGeneration = diagnosticEvent.LoopGeneration,
                recoveryCount = diagnosticEvent.RecoveryCount,
                hardwareDecodeConfirmed = diagnosticEvent.HardwareDecodeConfirmed,
            }) + Environment.NewLine;
            await File.AppendAllTextAsync(_path, line, Encoding.UTF8, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken);
        _gate.Release();
    }

    public ValueTask DisposeAsync()
    {
        _disposed = true;
        _gate.Dispose();
        return ValueTask.CompletedTask;
    }

    private void RotateIfNeeded()
    {
        if (!File.Exists(_path) || new FileInfo(_path).Length < _maximumBytes) return;
        for (var index = 4; index >= 1; index--)
        {
            var source = index == 1 ? _path : $"{_path}.{index - 1}";
            var destination = $"{_path}.{index}";
            if (File.Exists(source)) File.Move(source, destination, overwrite: true);
        }
    }
}
