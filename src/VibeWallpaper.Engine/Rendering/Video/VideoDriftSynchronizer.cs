using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Engine.Rendering.Video;

public interface IVideoPlaybackEndpoint
{
    string Id { get; }
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    void Seek(TimeSpan position);
}

public interface IVideoResumeObserver
{
    void NotifyResumed(string endpointId);
}

public interface IVideoSynchronizationEndpoint : IVideoPlaybackEndpoint
{
    void AttachResumeObserver(IVideoResumeObserver observer);
}

/// <summary>
/// Best-effort multi-decoder correction. It deliberately makes no frame-perfect synchronization claim.
/// </summary>
public sealed class VideoDriftSynchronizer : IVideoResumeObserver
{
    public static TimeSpan DefaultDriftThreshold { get; } = TimeSpan.FromMilliseconds(100);

    private readonly IEngineDispatcher _dispatcher;
    private readonly ILoopingPlaybackClock _clock;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _cooldown;
    private readonly Func<Task>? _resumeSample;
    private readonly Dictionary<string, long> _lastCorrection = new(StringComparer.Ordinal);
    private readonly HashSet<string> _resumeCorrections = new(StringComparer.Ordinal);

    public VideoDriftSynchronizer(
        IEngineDispatcher dispatcher,
        ILoopingPlaybackClock clock,
        TimeProvider? timeProvider = null,
        TimeSpan? cooldown = null,
        Func<Task>? resumeSample = null)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _cooldown = cooldown ?? TimeSpan.FromSeconds(1);
        _resumeSample = resumeSample;
        if (_cooldown < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cooldown));
    }

    public void NotifyResumed(string endpointId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointId);
        lock (_resumeCorrections) _resumeCorrections.Add(endpointId);
        if (_resumeSample is not null)
        {
            _ = ObserveImmediateSampleAsync(_resumeSample());
        }
    }

    public Task SampleAsync(
        IReadOnlyList<IVideoPlaybackEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Any(static endpoint => endpoint is null))
            throw new ArgumentException("Playback endpoints cannot contain null.", nameof(endpoints));
        return _dispatcher.InvokeAsync(token =>
        {
            token.ThrowIfCancellationRequested();
            var clockPosition = _clock.Position;
            var target = clockPosition.MediaPosition;
            var now = _timeProvider.GetTimestamp();
            foreach (var endpoint in endpoints)
            {
                var force = ConsumeResumeCorrection(endpoint.Id);
                var drift = LoopingPlaybackClock.Distance(endpoint.Position, target, _clock.Duration);
                if (!force && drift <= DefaultDriftThreshold) continue;
                if (!force && IsCoolingDown(endpoint.Id, now)) continue;
                endpoint.Seek(target);
                _lastCorrection[endpoint.Id] = now;
            }

            return ValueTask.CompletedTask;
        }, cancellationToken);
    }

    private bool ConsumeResumeCorrection(string id)
    {
        lock (_resumeCorrections) return _resumeCorrections.Remove(id);
    }

    private bool IsCoolingDown(string id, long now) =>
        _lastCorrection.TryGetValue(id, out var correctedAt) &&
        _timeProvider.GetElapsedTime(correctedAt, now) < _cooldown;

    private static async Task ObserveImmediateSampleAsync(Task sample)
    {
        try { await sample; }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { }
    }
}
