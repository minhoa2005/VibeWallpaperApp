namespace VibeWallpaper.Engine.Activity;

public sealed class RemoteDesktopObserver : IActivityObserver
{
    private readonly object _gate = new();
    private readonly IActivityEvidenceSink _sink;
    private readonly IDisposable? _registration;
    private bool _disposed;

    public RemoteDesktopObserver(IActivityEvidenceSink sink, IDisposable? registration = null)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _registration = registration;
    }

    public void SessionChanged()
    {
        lock (_gate)
        {
            if (!_disposed) _sink.Enqueue(new ActivityEvidence(ActivityEvidenceKind.RemoteDesktopChanged));
        }
    }

    public void Start()
    {
        lock (_gate) ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }

        _registration?.Dispose();
    }
}
