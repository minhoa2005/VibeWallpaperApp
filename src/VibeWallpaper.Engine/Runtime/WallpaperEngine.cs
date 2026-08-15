using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Runtime;

public sealed record EngineCommandResult(bool Success, string? ErrorCode, string? Message);

public sealed class WallpaperEngine : IWallpaperEngine, ILibraryStateAuthority, IAsyncDisposable
{
    private readonly IWallpaperEngine _inner;
    private readonly IReadOnlyList<IAsyncDisposable> _shutdownOrder;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _shutdownDeadline;
    private readonly FallbackRendererCoordinator? _fallback;
    private Task? _disposeTask;
    private readonly SemaphoreSlim _policyGate = new(1, 1);
    private readonly Dictionary<(string Output, PerformanceReasonOwner Owner), HashSet<PerformanceReason>> _appliedActivityReasons = [];
    private MonitorIdentity? _selectedPausedOutput;

    public WallpaperEngine(
        IWallpaperEngine inner,
        IEnumerable<IAsyncDisposable>? shutdownOrder = null,
        TimeSpan? shutdownDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(inner);
        _inner = inner;
        _shutdownOrder = shutdownOrder?.ToArray() ?? [];
        _shutdownDeadline = shutdownDeadline ?? TimeSpan.FromSeconds(5);
        if (_shutdownDeadline <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(shutdownDeadline));
    }

    public WallpaperEngine(
        IWallpaperEngine inner,
        FallbackRendererCoordinator fallback,
        IEnumerable<IAsyncDisposable>? shutdownOrder = null,
        TimeSpan? shutdownDeadline = null)
        : this(inner, shutdownOrder, shutdownDeadline)
    {
        ArgumentNullException.ThrowIfNull(fallback);
        _fallback = fallback;
    }

    public async Task<AssignmentResult> ApplyAsync(AssignmentRequest request, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        var result = await _inner.ApplyAsync(request, linked.Token);
        if (result.Outcome == AssignmentOutcome.Applied && result.Persisted)
        {
            _fallback?.UpdatePersistedState(_inner.GetSnapshot().State);
        }

        return result;
    }

    public async Task SetReasonsAsync(MonitorIdentity output, PerformanceReasonOwner owner, IReadOnlySet<PerformanceReason> reasons, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        await _inner.SetReasonsAsync(output, owner, reasons, linked.Token);
    }

    public bool IsPaused { get; private set; }

    public async Task ApplyActivitySnapshotAsync(
        IReadOnlyList<MonitorIdentity> activeOutputs,
        ActivitySnapshot snapshot,
        PerformancePolicyOptions options,
        MonitorIdentity? selectedPausedOutput,
        IReadOnlySet<PerformanceReason> globalSafetyReasons,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activeOutputs);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(globalSafetyReasons);
        if (activeOutputs.Any(static output => output is null))
            throw new ArgumentException("Active outputs cannot contain null.", nameof(activeOutputs));
        if (globalSafetyReasons.Any(static reason => !Enum.IsDefined(reason)))
            throw new ArgumentException("Global safety reasons must be defined.", nameof(globalSafetyReasons));

        await _policyGate.WaitAsync(cancellationToken);
        try
        {
            _selectedPausedOutput = selectedPausedOutput;
            var uniqueOutputs = activeOutputs.Distinct().ToArray();
            foreach (var output in uniqueOutputs)
            {
                var activityReasons = MapActivityReasons(snapshot, output, options, globalSafetyReasons);
                await ApplyReasonsIfChangedAsync(
                    output,
                    PerformanceReasonOwner.Activity,
                    activityReasons,
                    cancellationToken);

                var userReasons = UserReasonsFor(output, IsPaused, _selectedPausedOutput);
                await ApplyReasonsIfChangedAsync(
                    output,
                    PerformanceReasonOwner.User,
                    userReasons,
                    cancellationToken);
            }

            var activeKeys = uniqueOutputs.Select(static output => output.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var key in _appliedActivityReasons.Keys.Where(key => !activeKeys.Contains(key.Output)).ToArray())
                _appliedActivityReasons.Remove(key);
        }
        finally
        {
            _policyGate.Release();
        }
    }

    public async Task<EngineCommandResult> SetPausedAllAsync(
        IReadOnlyList<MonitorIdentity> outputs,
        bool paused,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        await _policyGate.WaitAsync(cancellationToken);
        try
        {
            var completed = new List<MonitorIdentity>();
            var previousPauseAll = IsPaused;
            try
            {
                foreach (var output in outputs)
                {
                    await ApplyReasonsIfChangedAsync(
                        output,
                        PerformanceReasonOwner.User,
                        UserReasonsFor(output, paused, _selectedPausedOutput),
                        cancellationToken);
                    completed.Add(output);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                try
                {
                    foreach (var output in completed.AsEnumerable().Reverse())
                    {
                        var rollbackReasons = UserReasonsFor(output, previousPauseAll, _selectedPausedOutput);
                        await SetReasonsAsync(output, PerformanceReasonOwner.User, rollbackReasons, CancellationToken.None);
                        _appliedActivityReasons[(output.Key, PerformanceReasonOwner.User)] = [.. rollbackReasons];
                    }
                }
                catch (Exception rollbackFailure)
                {
                    return new EngineCommandResult(false, "wallpaper.pause.rollback_failed", rollbackFailure.Message);
                }

                return new EngineCommandResult(false, "wallpaper.pause.failed", exception.Message);
            }

            IsPaused = paused;
            return new EngineCommandResult(true, null, paused ? "Paused all outputs." : "Resumed all outputs.");
        }
        finally
        {
            _policyGate.Release();
        }
    }

    public EngineSnapshot GetSnapshot()
    {
        var snapshot = _inner.GetSnapshot();
        if (_fallback is null)
        {
            return new EngineSnapshot(snapshot.State, snapshot.Outputs);
        }

        var effective = _fallback.GetSnapshot();
        return new EngineSnapshot(
            snapshot.State,
            snapshot.Outputs.Select(output => output with
            {
                EffectiveState = effective.TryGetValue(output.Output.Key, out var state) ? state : output.EffectiveState,
            }).ToArray());
    }

    public LibraryStateSnapshot GetLibrarySnapshot() => LibraryAuthority.GetLibrarySnapshot();

    public async Task<LibraryStateSnapshot> AddLibraryItemAsync(
        VibeWallpaper.Engine.Core.Persistence.WallpaperLibraryItem item,
        CancellationToken cancellationToken)
    {
        var snapshot = await LibraryAuthority.AddLibraryItemAsync(item, cancellationToken);
        UpdateFallbackPersistedState();
        return snapshot;
    }

    public async Task<LibraryStateSnapshot> ReplaceLibraryItemAsync(
        VibeWallpaper.Engine.Core.Persistence.WallpaperLibraryItem item,
        CancellationToken cancellationToken)
    {
        var snapshot = await LibraryAuthority.ReplaceLibraryItemAsync(item, cancellationToken);
        UpdateFallbackPersistedState();
        return snapshot;
    }

    public async Task<LibraryStateSnapshot> RemoveLibraryItemAsync(
        WallpaperId id,
        bool clearAssignments,
        CancellationToken cancellationToken)
    {
        var snapshot = await LibraryAuthority.RemoveLibraryItemAsync(id, clearAssignments, cancellationToken);
        UpdateFallbackPersistedState();
        return snapshot;
    }

    public async Task<LibraryStateSnapshot> SetWebNetworkPermissionAsync(
        WallpaperId id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var snapshot = await LibraryAuthority.SetWebNetworkPermissionAsync(id, enabled, cancellationToken);
        UpdateFallbackPersistedState();
        return snapshot;
    }

    public Task<FallbackInitializationResult> InitializeFallbacksAsync(
        Func<WallpaperKind, bool> rendererAvailable,
        CancellationToken cancellationToken) =>
        (_fallback ?? throw new InvalidOperationException("No fallback coordinator is configured."))
            .InitializeAsync(rendererAvailable, cancellationToken);

    public Task ReconcileOutputAsync(
        MonitorIdentity output,
        VibeWallpaper.Engine.Core.Persistence.SourceValidationStatus sourceStatus,
        bool rendererAvailable,
        CancellationToken cancellationToken) =>
        (_fallback ?? throw new InvalidOperationException("No fallback coordinator is configured."))
            .ReconcileAsync(output, sourceStatus, rendererAvailable, cancellationToken);

    public ValueTask DisposeAsync() => new(_disposeTask ??= DisposeOnceAsync());

    private ILibraryStateAuthority LibraryAuthority =>
        _inner as ILibraryStateAuthority
        ?? throw new InvalidOperationException("The wallpaper engine does not expose library state operations.");

    private void UpdateFallbackPersistedState() =>
        _fallback?.UpdatePersistedState(_inner.GetSnapshot().State);

    private async Task DisposeOnceAsync()
    {
        await _shutdown.CancelAsync();
        var deadline = DateTime.UtcNow + _shutdownDeadline;
        if (_inner is WallpaperAssignmentCoordinator coordinator)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    var shutdown = Task.Run(() => coordinator.ShutdownAsync());
                    await shutdown.WaitAsync(remaining);
                }
                catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
                {
                }
            }
        }

        foreach (var resource in _shutdownOrder)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            try
            {
                var disposal = Task.Run(async () => await resource.DisposeAsync());
                await disposal.WaitAsync(remaining);
            }
            catch (Exception exception) when (exception is TimeoutException or OperationCanceledException) { break; }
        }
        _policyGate.Dispose();
        _shutdown.Dispose();
    }

    private async Task ApplyReasonsIfChangedAsync(
        MonitorIdentity output,
        PerformanceReasonOwner owner,
        HashSet<PerformanceReason> reasons,
        CancellationToken cancellationToken)
    {
        var key = (output.Key, owner);
        if (_appliedActivityReasons.TryGetValue(key, out var existing) && existing.SetEquals(reasons)) return;
        await SetReasonsAsync(output, owner, reasons, cancellationToken);
        _appliedActivityReasons[key] = [.. reasons];
    }

    private static HashSet<PerformanceReason> MapActivityReasons(
        ActivitySnapshot snapshot,
        MonitorIdentity output,
        PerformancePolicyOptions options,
        IReadOnlySet<PerformanceReason> globalSafetyReasons)
    {
        var reasons = new HashSet<PerformanceReason>(globalSafetyReasons);
        if (snapshot.RunningOnBattery) reasons.Add(PerformanceReason.Battery);
        if (snapshot.BatterySaverEnabled) reasons.Add(PerformanceReason.BatterySaver);
        if (options.SuspendOnFullscreen && snapshot.FullscreenCoveredOutputs.Contains(output)) reasons.Add(PerformanceReason.FullscreenCovered);
        if (options.SuspendOnMaximized && snapshot.MaximizedOutputs.Contains(output)) reasons.Add(PerformanceReason.MaximizedCovered);
        if (options.SuspendOnRemoteDesktop && snapshot.RemoteDesktopSession) reasons.Add(PerformanceReason.RemoteDesktop);
        if (options.SuspendOnSessionLock && snapshot.SessionLocked) reasons.Add(PerformanceReason.SessionLocked);
        if (options.SuspendOnDisplayOff && snapshot.DisplayOff) reasons.Add(PerformanceReason.DisplayOff);
        if (options.SuspendOnSystemSleep && snapshot.SystemSleeping) reasons.Add(PerformanceReason.SystemSleeping);
        return reasons;
    }

    private static HashSet<PerformanceReason> UserReasonsFor(
        MonitorIdentity output,
        bool pauseAll,
        MonitorIdentity? selectedPausedOutput) =>
        pauseAll || selectedPausedOutput == output
            ? [PerformanceReason.UserPaused]
            : [];
}
