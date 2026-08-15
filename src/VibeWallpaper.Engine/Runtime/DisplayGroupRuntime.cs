using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Rendering.Video;

namespace VibeWallpaper.Engine.Runtime;

public sealed class DisplayGroupRuntime
{
    private CancellationTokenSource? _transitionCancellation;
    private long _generation;
    private ITimer? _samplingTimer;

    public DisplayGroupRuntime(DisplayGroupId identity) => Identity = identity;

    public DisplayGroupId Identity { get; }
    public long Generation => _generation;
    public AsyncCommitGate CommitGate { get; } = new();
    public IReadOnlyList<MonitorIdentity> Members { get; internal set; } = [];
    internal LoopingPlaybackClock? PlaybackClock { get; private set; }
    internal VideoDriftSynchronizer? DriftSynchronizer { get; private set; }

    internal void SetSynchronization(
        LoopingPlaybackClock? playbackClock,
        VideoDriftSynchronizer? driftSynchronizer,
        TimeProvider timeProvider,
        Func<Task> sample)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(sample);
        if ((playbackClock is null) != (driftSynchronizer is null))
        {
            throw new ArgumentException("Playback clock and drift synchronizer must both be provided or both be null.");
        }

        PlaybackClock = playbackClock;
        DriftSynchronizer = driftSynchronizer;
        if (playbackClock is null)
        {
            _samplingTimer?.Dispose();
            _samplingTimer = null;
            return;
        }

        _samplingTimer ??= timeProvider.CreateTimer(
            static state => _ = ObserveSampleAsync(((Func<Task>)state!).Invoke()),
            sample,
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(1));
    }

    internal (long Generation, CancellationToken Token) BeginTransition(
        IReadOnlyList<MonitorIdentity> members,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
    {
        _transitionCancellation?.Cancel();
        _transitionCancellation?.Dispose();
        _transitionCancellation = CancellationTokenSource.CreateLinkedTokenSource(callerToken, shutdownToken);
        Members = members.ToArray();
        return (++_generation, _transitionCancellation.Token);
    }

    internal void InvalidateTransition()
    {
        ++_generation;
        _transitionCancellation?.Cancel();
    }

    internal void Deactivate()
    {
        InvalidateTransition();
        Members = [];
        _samplingTimer?.Dispose();
        _samplingTimer = null;
    }

    internal void Dispose()
    {
        Deactivate();
        _transitionCancellation?.Dispose();
        _transitionCancellation = null;
    }

    internal bool IsCurrent(long generation) => generation == _generation;

    private static async Task ObserveSampleAsync(Task sample)
    {
        try { await sample; }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { }
    }
}
