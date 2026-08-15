using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Engine.Rendering.Video;

public interface IVideoAudioEndpoint
{
    MonitorIdentity Output { get; }
    bool IsConnected { get; }
    bool IsActiveVideo { get; }
    bool IsSuspended { get; }
    int PersistedVolumePercent { get; }
    bool IsMuted { get; }
    int VolumePercent { get; }
    void SetMuted(bool muted);
    void SetVolume(int volumePercent);
}

public sealed class GlobalAudioOwnershipPolicy
{
    private readonly IStateStore _stateStore;
    private readonly SemaphoreSlim _transactionGate = new(1, 1);

    public GlobalAudioOwnershipPolicy(IStateStore stateStore) =>
        _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));

    public async Task<PersistedState> SelectOwnerAsync(
        PersistedState current,
        MonitorIdentity? selectedOwner,
        IReadOnlyList<IVideoAudioEndpoint> endpoints,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        ValidateEndpoints(endpoints);
        await _transactionGate.WaitAsync(cancellationToken);
        try
        {
            var next = new PersistedState(
                current.SchemaVersion,
                current.Library,
                current.Assignments,
                current.Groups,
                selectedOwner);

            var snapshots = Capture(endpoints);
            try
            {
                ApplyCore(next, endpoints);
                await _stateStore.SaveAsync(next, cancellationToken);
                return next;
            }
            catch
            {
                Restore(snapshots);
                throw;
            }
        }
        finally
        {
            _transactionGate.Release();
        }
    }

    public void Apply(PersistedState state, IReadOnlyList<IVideoAudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(state);
        ValidateEndpoints(endpoints);
        var snapshots = Capture(endpoints);
        try
        {
            ApplyCore(state, endpoints);
        }
        catch
        {
            Restore(snapshots);
            throw;
        }
    }

    private static void ApplyCore(PersistedState state, IReadOnlyList<IVideoAudioEndpoint> endpoints)
    {
        var ownerKey = state.AudioOwner?.Key;
        var selected = ownerKey is null
            ? null
            : endpoints.SingleOrDefault(endpoint => string.Equals(endpoint.Output.Key, ownerKey, StringComparison.Ordinal));
        var audible = selected is { IsConnected: true, IsActiveVideo: true, IsSuspended: false } ? selected : null;

        // Old/non-owner players are muted first, globally, before the selected player can unmute.
        foreach (var endpoint in endpoints.Where(endpoint => !ReferenceEquals(endpoint, audible)))
        {
            if (!endpoint.IsMuted) endpoint.SetMuted(true);
        }

        if (audible is not null)
        {
            audible.SetVolume(audible.PersistedVolumePercent);
            if (audible.IsMuted) audible.SetMuted(false);
        }
    }

    private static IReadOnlyList<AudioSnapshot> Capture(IReadOnlyList<IVideoAudioEndpoint> endpoints) =>
        endpoints.Select(static endpoint => new AudioSnapshot(endpoint, endpoint.IsMuted, endpoint.VolumePercent)).ToArray();

    private static void Restore(IReadOnlyList<AudioSnapshot> snapshots)
    {
        // First silence anything that was not previously audible, then restore volumes, and only
        // then restore the former audible endpoint. This preserves the no-overlap invariant.
        foreach (var snapshot in snapshots.Where(static snapshot => snapshot.WasMuted))
        {
            TryRestore(() =>
            {
                if (!snapshot.Endpoint.IsMuted) snapshot.Endpoint.SetMuted(true);
            });
        }

        foreach (var snapshot in snapshots)
        {
            TryRestore(() =>
            {
                if (snapshot.Endpoint.VolumePercent != snapshot.VolumePercent)
                    snapshot.Endpoint.SetVolume(snapshot.VolumePercent);
            });
        }

        foreach (var snapshot in snapshots.Where(static snapshot => !snapshot.WasMuted))
        {
            TryRestore(() =>
            {
                if (snapshot.Endpoint.IsMuted) snapshot.Endpoint.SetMuted(false);
            });
        }
    }

    private static void TryRestore(Action restore)
    {
        try { restore(); }
        catch { }
    }

    private static void ValidateEndpoints(IReadOnlyList<IVideoAudioEndpoint> endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (endpoints.Any(static endpoint => endpoint is null))
            throw new ArgumentException("Audio endpoints cannot contain null.", nameof(endpoints));
        if (endpoints.Select(static endpoint => endpoint.Output.Key).Distinct(StringComparer.Ordinal).Count() != endpoints.Count)
            throw new ArgumentException("Audio endpoints must represent unique logical outputs.", nameof(endpoints));
    }

    private sealed record AudioSnapshot(IVideoAudioEndpoint Endpoint, bool WasMuted, int VolumePercent);
}
