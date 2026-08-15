using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Activity;

public enum ActivityEvidenceKind
{
    ForegroundChanged,
    ZOrderChanged,
    LocationChanged,
    FullscreenChanged,
    TopologyReconciled,
    SessionLocked,
    SessionUnlocked,
    SystemSleeping,
    SystemResumed,
    DisplayOff,
    DisplayOn,
    PowerChanged,
    RemoteDesktopChanged,
    ExplicitPauseChanged,
    HostInvalidated,
    MonitorRemoved,
    Shutdown,
}

public sealed record ActivityEvidence
{
    public ActivityEvidenceKind Kind { get; }
    public MonitorIdentity? Output { get; }

    public ActivityEvidence(ActivityEvidenceKind kind, MonitorIdentity? output = null)
    {
        if (!Enum.IsDefined(kind)) throw new ArgumentException("A defined evidence kind is required.", nameof(kind));
        Kind = kind;
        Output = output;
    }

    public bool RequiresImmediateEvaluation => Kind is
        ActivityEvidenceKind.SessionLocked or
        ActivityEvidenceKind.SessionUnlocked or
        ActivityEvidenceKind.SystemSleeping or
        ActivityEvidenceKind.SystemResumed or
        ActivityEvidenceKind.DisplayOff or
        ActivityEvidenceKind.DisplayOn or
        ActivityEvidenceKind.PowerChanged or
        ActivityEvidenceKind.RemoteDesktopChanged or
        ActivityEvidenceKind.ExplicitPauseChanged or
        ActivityEvidenceKind.HostInvalidated or
        ActivityEvidenceKind.MonitorRemoved or
        ActivityEvidenceKind.Shutdown;
}

public delegate void ActivitySnapshotPublishedHandler(object? sender, ActivitySnapshot snapshot);

public interface IActivityMonitor : IDisposable
{
    event ActivitySnapshotPublishedHandler? SnapshotPublished;

    ActivitySnapshot? Current { get; }

    void Start();

    void Enqueue(ActivityEvidence evidence);

    void Stop();
}

public interface IActivityEvidenceSink
{
    void Enqueue(ActivityEvidence evidence);
}

public interface IActivityEvidenceConsumer
{
    void Apply(ActivityEvidence evidence);
}

public interface IActivityObserver : IDisposable
{
    void Start();
}
