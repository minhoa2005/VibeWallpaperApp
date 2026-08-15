using System.Globalization;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Video;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;

namespace VibeWallpaper.Engine.Runtime;

public sealed class WallpaperAssignmentCoordinator : IWallpaperEngine, ILibraryStateAuthority
{
    private readonly IEngineDispatcher _dispatcher;
    private readonly IRendererFactory _rendererFactory;
    private readonly IStateStore _stateStore;
    private readonly PerformancePolicyOptions _policyOptions;
    private readonly GlobalAudioOwnershipPolicy _audioPolicy;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<string, MonitorRuntime> _monitors = new(StringComparer.Ordinal);
    private readonly Dictionary<DisplayGroupId, DisplayGroupRuntime> _groups = [];
    private readonly AsyncCommitGate _stateCommitGate = new();
    private readonly object _snapshotGate = new();
    private readonly IVideoPlaybackDiagnostics _diagnostics;
    private PersistedState _state;
    private EngineSnapshot _publishedSnapshot;
    private long _libraryVersion;

    public WallpaperAssignmentCoordinator(
        IEngineDispatcher dispatcher,
        IRendererFactory rendererFactory,
        IStateStore stateStore,
        PersistedState? initialState = null,
        PerformancePolicyOptions? policyOptions = null,
        TimeProvider? timeProvider = null,
        IVideoPlaybackDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(rendererFactory);
        ArgumentNullException.ThrowIfNull(stateStore);
        _dispatcher = dispatcher;
        _rendererFactory = rendererFactory;
        _stateStore = stateStore;
        _audioPolicy = new GlobalAudioOwnershipPolicy(stateStore);
        _state = initialState ?? PersistedState.Default;
        _publishedSnapshot = new EngineSnapshot(_state, []);
        _policyOptions = policyOptions ?? PerformancePolicyOptions.Default;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _diagnostics = diagnostics ?? LogSinkVideoPlaybackDiagnostics.None;
    }

    public Task<AssignmentResult> ApplyAsync(
        AssignmentRequest request,
        CancellationToken cancellationToken) =>
        ApplyCoreAsync(request, persistLogicalState: true, cancellationToken);

    public Task<AssignmentResult> ApplyRuntimeOnlyAsync(
        AssignmentRequest request,
        CancellationToken cancellationToken) =>
        ApplyCoreAsync(request, persistLogicalState: false, cancellationToken);

    private Task<AssignmentResult> ApplyCoreAsync(
        AssignmentRequest request,
        bool persistLogicalState,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher.InvokeAsync(
            dispatcherToken => ApplyOnDispatcherAsync(
                request,
                persistLogicalState,
                cancellationToken,
                dispatcherToken),
            CancellationToken.None);
    }

    public Task SetReasonsAsync(
        MonitorIdentity output,
        PerformanceReasonOwner owner,
        IReadOnlySet<PerformanceReason> reasons,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(reasons);
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher.InvokeAsync(
            token => SetReasonsOnDispatcherAsync(output, owner, reasons, cancellationToken, token),
            CancellationToken.None);
    }

    public EngineSnapshot GetSnapshot()
    {
        lock (_snapshotGate)
        {
            return _publishedSnapshot;
        }
    }

    public LibraryStateSnapshot GetLibrarySnapshot()
    {
        lock (_snapshotGate)
        {
            return new LibraryStateSnapshot(
                _libraryVersion,
                _state.Library.ToArray(),
                _state.Assignments
                    .Select(static assignment => assignment.Wallpaper)
                    .ToHashSet());
        }
    }

    public Task<LibraryStateSnapshot> AddLibraryItemAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return MutateLibraryAsync(current =>
        {
            if (current.Library.Any(existing => existing.Definition.Id == item.Definition.Id))
            {
                throw new LibraryStateException(
                    "library.item.duplicate",
                    "The wallpaper already exists in the library.");
            }

            return new PersistedState(
                current.SchemaVersion,
                [.. current.Library, item],
                current.Assignments,
                current.Groups,
                current.AudioOwner);
        }, cancellationToken);
    }

    public Task<LibraryStateSnapshot> ReplaceLibraryItemAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return MutateLibraryAsync(current =>
        {
            var index = FindLibraryIndex(current.Library, item.Definition.Id);
            var library = current.Library.ToArray();
            library[index] = item;
            return new PersistedState(
                current.SchemaVersion,
                library,
                current.Assignments,
                current.Groups,
                current.AudioOwner);
        }, cancellationToken);
    }

    public Task<LibraryStateSnapshot> SetWebNetworkPermissionAsync(
        WallpaperId id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ValidateWallpaperId(id);
        return MutateLibraryAsync(current =>
        {
            var index = FindLibraryIndex(current.Library, id);
            var item = current.Library[index];
            if (item.Definition.Source is not WebSource)
            {
                throw new LibraryStateException(
                    "library.network.web_required",
                    "Network permission applies only to web wallpapers.");
            }

            var definition = item.Definition;
            var updatedDefinition = new WallpaperDefinition(
                definition.Id,
                definition.Name,
                definition.Source,
                definition.Fit,
                definition.TargetFps,
                enabled,
                definition.AudioEnabled,
                definition.VolumePercent,
                definition.InteractionEnabled);
            var library = current.Library.ToArray();
            library[index] = new WallpaperLibraryItem(
                updatedDefinition,
                item.ThumbnailCachePath,
                item.Video,
                item.Validation);
            return new PersistedState(
                current.SchemaVersion,
                library,
                current.Assignments,
                current.Groups,
                current.AudioOwner);
        }, cancellationToken);
    }

    public Task<LibraryStateSnapshot> RemoveLibraryItemAsync(
        WallpaperId id,
        bool clearAssignments,
        CancellationToken cancellationToken)
    {
        ValidateWallpaperId(id);
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher.InvokeAsync(
            shutdownToken => new ValueTask<LibraryStateSnapshot>(
                RemoveLibraryItemOnDispatcherAsync(
                    id,
                    clearAssignments,
                    cancellationToken,
                    shutdownToken)),
            CancellationToken.None);
    }

    public Task SelectAudioOwnerAsync(
        MonitorIdentity? selectedOwner,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher.InvokeAsync(async shutdownToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdownToken);
            using var lease = await _stateCommitGate.EnterAsync(linked.Token);
            PersistedState current;
            lock (_snapshotGate) current = _state;
            var next = await _audioPolicy.SelectOwnerAsync(
                current,
                selectedOwner,
                ActiveAudioEndpoints(),
                linked.Token);
            lock (_snapshotGate) _state = next;
            PublishSnapshot();
        }, CancellationToken.None);
    }

    private Task<LibraryStateSnapshot> MutateLibraryAsync(
        Func<PersistedState, PersistedState> mutate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher.InvokeAsync(async shutdownToken =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                shutdownToken);
            using var lease = await _stateCommitGate.EnterAsync(linked.Token);
            PersistedState current;
            lock (_snapshotGate) current = _state;
            var next = mutate(current);
            await _stateStore.SaveAsync(next, linked.Token);
            lock (_snapshotGate)
            {
                _state = next;
                _libraryVersion++;
            }
            PublishSnapshot();
            return GetLibrarySnapshot();
        }, CancellationToken.None);
    }

    private async Task<LibraryStateSnapshot> RemoveLibraryItemOnDispatcherAsync(
        WallpaperId id,
        bool clearAssignments,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            shutdownToken);
        List<IWallpaperRenderer> detachedRenderers;
        LibraryStateSnapshot snapshot;
        using (var lease = await _stateCommitGate.EnterAsync(linked.Token))
        {
            PersistedState current;
            lock (_snapshotGate) current = _state;
            _ = FindLibraryIndex(current.Library, id);
            var affectedAssignments = current.Assignments
                .Where(assignment => assignment.Wallpaper == id)
                .ToArray();
            if (affectedAssignments.Length > 0 && !clearAssignments)
            {
                throw new LibraryStateException(
                    "library.item.assigned",
                    "The wallpaper is assigned to one or more outputs.");
            }

            var affectedOutputKeys = affectedAssignments
                .Select(assignment => assignment.Monitor.Identity.Key)
                .ToHashSet(StringComparer.Ordinal);
            var removedGroupIds = current.Groups
                .Where(group => group.Wallpaper == id)
                .Select(group => group.Id)
                .ToArray();
            var next = new PersistedState(
                current.SchemaVersion,
                current.Library.Where(item => item.Definition.Id != id).ToArray(),
                current.Assignments.Where(assignment => assignment.Wallpaper != id).ToArray(),
                current.Groups.Where(group => group.Wallpaper != id).ToArray(),
                current.AudioOwner is not null && affectedOutputKeys.Contains(current.AudioOwner.Key)
                    ? null
                    : current.AudioOwner);
            await _stateStore.SaveAsync(next, linked.Token);

            detachedRenderers = [];
            foreach (var outputKey in affectedOutputKeys)
            {
                if (!_monitors.TryGetValue(outputKey, out var runtime)) continue;
                runtime.InvalidateTransition();
                if (runtime.ActiveRenderer is { } active) detachedRenderers.Add(active);
                detachedRenderers.AddRange(runtime.InFlightCandidates.Values);
                runtime.ActiveRenderer = null;
                runtime.InFlightCandidates.Clear();
            }

            foreach (var groupId in removedGroupIds)
            {
                if (!_groups.Remove(groupId, out var group)) continue;
                group.Dispose();
            }

            lock (_snapshotGate)
            {
                _state = next;
                _libraryVersion++;
            }
            PublishSnapshot();
            snapshot = GetLibrarySnapshot();
        }

        Exception? cleanupFailure = null;
        foreach (var renderer in detachedRenderers.Distinct())
        {
            try
            {
                await TryStopAsync(renderer);
                await renderer.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure ??= exception;
            }
        }

        if (cleanupFailure is not null)
        {
            throw new LibraryStateException(
                "library.runtime.cleanup_failed",
                "The library item was removed, but renderer cleanup did not complete.");
        }

        return snapshot;
    }

    private static int FindLibraryIndex(
        IReadOnlyList<WallpaperLibraryItem> library,
        WallpaperId id)
    {
        for (var index = 0; index < library.Count; index++)
        {
            if (library[index].Definition.Id == id) return index;
        }

        throw new LibraryStateException(
            "library.item.missing",
            "The wallpaper was not found in the library.");
    }

    private static void ValidateWallpaperId(WallpaperId id)
    {
        if (id.Value == Guid.Empty)
        {
            throw new ArgumentException("A wallpaper ID is required.", nameof(id));
        }
    }

    public Task SynchronizeGroupAsync(
        DisplayGroupId groupId,
        CancellationToken cancellationToken = default)
    {
        if (groupId.Value == Guid.Empty) throw new ArgumentException("Group ID is required.", nameof(groupId));
        cancellationToken.ThrowIfCancellationRequested();
        return _dispatcher.InvokeAsync(
            token => SynchronizeGroupOnDispatcherAsync(groupId, token),
            cancellationToken);
    }

    public Task ShutdownAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(async token =>
        {
            foreach (var group in _groups.Values) group.Dispose();
            foreach (var runtime in _monitors.Values) runtime.InvalidateTransition();
            var renderers = _monitors.Values
                .SelectMany(static runtime => runtime.InFlightCandidates.Values.Append(runtime.ActiveRenderer))
                .Where(static renderer => renderer is not null)
                .Cast<IWallpaperRenderer>()
                .Distinct()
                .ToArray();
            foreach (var runtime in _monitors.Values)
            {
                runtime.ActiveRenderer = null;
                runtime.InFlightCandidates.Clear();
            }
            foreach (var renderer in renderers)
            {
                await TryStopAsync(renderer);
                await renderer.DisposeAsync();
            }
            PublishSnapshot();
        }, cancellationToken);

    private ValueTask<AssignmentResult> ApplyOnDispatcherAsync(
        AssignmentRequest request,
        bool persistLogicalState,
        CancellationToken callerToken,
        CancellationToken shutdownToken) =>
        request.Targets.Count == 1 && request.Mode == DisplayMode.Independent
            ? new ValueTask<AssignmentResult>(ApplySingleAsync(request, persistLogicalState, callerToken, shutdownToken))
            : new ValueTask<AssignmentResult>(ApplyGroupAsync(request, persistLogicalState, callerToken, shutdownToken));

    private async Task<AssignmentResult> ApplySingleAsync(
        AssignmentRequest request,
        bool persistLogicalState,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
    {
        var target = request.Targets[0];
        var runtime = GetMonitor(target.Monitor);
        SupersedeGroupsContaining(target.Monitor);
        var transition = runtime.BeginTransition(target, callerToken, shutdownToken);
        var candidate = new CandidateAttempt(
            transition.Generation,
            target,
            _rendererFactory.Create(request.Wallpaper));
        runtime.InFlightCandidates[transition.Generation] = candidate.Renderer;

        try
        {
            await PrepareAsync(candidate, request, transition.Token);
            if (!IsCurrent(runtime, transition.Generation))
            {
                await DisposeCandidateAsync(candidate);
                return Superseded(transition.Generation, target.Monitor);
            }

            if (IsUnavailable(runtime))
            {
                await DisposeCandidateAsync(candidate);
                return HostUnavailable(transition.Generation, target.Monitor);
            }

            await ApplyCurrentPolicyAsync(runtime, candidate.Renderer, transition.Token);
            if (!IsCurrent(runtime, transition.Generation))
            {
                await DisposeCandidateAsync(candidate);
                return Superseded(transition.Generation, target.Monitor);
            }

            using var lease = await runtime.CommitGate.EnterAsync(transition.Token);
            if (!IsCurrent(runtime, transition.Generation) || IsUnavailable(runtime))
            {
                await DisposeCandidateAsync(candidate);
                return IsUnavailable(runtime)
                    ? HostUnavailable(transition.Generation, target.Monitor)
                    : Superseded(transition.Generation, target.Monitor);
            }

            await ApplyCurrentPolicyAsync(runtime, candidate.Renderer, transition.Token);
            if (!IsCurrent(runtime, transition.Generation) || IsUnavailable(runtime))
            {
                await DisposeCandidateAsync(candidate);
                return IsUnavailable(runtime)
                    ? HostUnavailable(transition.Generation, target.Monitor)
                    : Superseded(transition.Generation, target.Monitor);
            }

            transition.Token.ThrowIfCancellationRequested();
            var oldRenderer = runtime.ActiveRenderer;
            var swapped = false;
            try
            {
                await candidate.Renderer.ActivateAsync(transition.Token);
                swapped = true;
                if (!IsCurrent(runtime, transition.Generation) || IsUnavailable(runtime))
                {
                    await RollbackSingleAsync(oldRenderer, candidate);
                    return IsUnavailable(runtime)
                        ? HostUnavailable(transition.Generation, target.Monitor)
                        : Superseded(transition.Generation, target.Monitor);
                }

                var persisted = !persistLogicalState || await PersistAsync(
                    request,
                    transition.Token,
                    () => IsCurrent(runtime, transition.Generation) &&
                        !IsUnavailable(runtime) &&
                        !callerToken.IsCancellationRequested &&
                        !shutdownToken.IsCancellationRequested);
                if (!persisted)
                {
                    await RollbackSingleAsync(oldRenderer, candidate);
                    callerToken.ThrowIfCancellationRequested();
                    shutdownToken.ThrowIfCancellationRequested();
                    return IsUnavailable(runtime)
                        ? HostUnavailable(transition.Generation, target.Monitor)
                        : Superseded(transition.Generation, target.Monitor);
                }

                runtime.ActiveRenderer = candidate.Renderer;
                candidate.Committed = true;
                runtime.InFlightCandidates.Remove(transition.Generation);
                MuteRetiredAudio(oldRenderer);
                _audioPolicy.Apply(_state, ActiveAudioEndpoints());
                await StopAndDisposeOldAsync(oldRenderer, target.Monitor);
                return new AssignmentResult(
                    transition.Generation,
                    AssignmentOutcome.Applied,
                    [target.Monitor],
                    persistLogicalState,
                    []);
            }
            catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
            {
                if (swapped) await RollbackSingleAsync(oldRenderer, candidate);
                else await DisposeCandidateAsync(candidate);
                return IsUnavailable(runtime)
                    ? HostUnavailable(transition.Generation, target.Monitor)
                    : Superseded(transition.Generation, target.Monitor);
            }
            catch (OperationCanceledException)
            {
                if (swapped) await RollbackSingleAsync(oldRenderer, candidate);
                else await DisposeCandidateAsync(candidate);
                throw;
            }
            catch (Exception failure)
            {
                if (swapped) await RollbackSingleAsync(oldRenderer, candidate);
                else await DisposeCandidateAsync(candidate);
                throw ActivationFailure("Wallpaper assignment commit failed.", failure);
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
        {
            await DisposeCandidateAsync(candidate);
            return IsUnavailable(runtime)
                ? HostUnavailable(transition.Generation, target.Monitor)
                : Superseded(transition.Generation, target.Monitor);
        }
        catch (OperationCanceledException)
        {
            await DisposeCandidateAsync(candidate);
            throw;
        }
        catch (WallpaperActivationException)
        {
            throw;
        }
        catch (Exception failure)
        {
            await DisposeCandidateAsync(candidate);
            throw ActivationFailure("Wallpaper candidate preparation failed.", failure);
        }
        finally
        {
            runtime.InFlightCandidates.Remove(transition.Generation);
            PublishSnapshot();
        }
    }

    private async Task<AssignmentResult> ApplyGroupAsync(
        AssignmentRequest request,
        bool persistLogicalState,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
    {
        var groupId = request.GroupId!.Value;
        if (!_groups.TryGetValue(groupId, out var group))
        {
            group = new DisplayGroupRuntime(groupId);
            _groups.Add(groupId, group);
        }

        var groupTransition = group.BeginTransition(
            request.Targets.Select(static target => target.Monitor).ToArray(),
            callerToken,
            shutdownToken);
        var attempts = new List<GroupCandidate>(request.Targets.Count);
        foreach (var target in request.Targets)
        {
            SupersedeGroupsContaining(target.Monitor, groupId);
            var runtime = GetMonitor(target.Monitor);
            var monitorTransition = runtime.BeginTransition(target, callerToken, groupTransition.Token);
            var renderer = _rendererFactory.Create(request.Wallpaper);
            var candidate = new CandidateAttempt(
                monitorTransition.Generation,
                target,
                renderer);
            runtime.InFlightCandidates[monitorTransition.Generation] = candidate.Renderer;
            attempts.Add(new GroupCandidate(runtime, monitorTransition.Generation, monitorTransition.Token, candidate));
        }

        using var groupWorkCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            [groupTransition.Token, .. attempts.Select(static item => item.TransitionToken)]);
        var groupWorkToken = groupWorkCancellation.Token;

        try
        {
            await Task.WhenAll(attempts.Select(PrepareGroupCandidateAsync));
            if (!IsCurrent(group, groupTransition.Generation, attempts))
            {
                await DisposeAllAsync(attempts);
                return Superseded(groupTransition.Generation, null);
            }

            var unavailable = attempts.FirstOrDefault(static item => IsUnavailable(item.Runtime));
            if (unavailable is not null)
            {
                await DisposeAllAsync(attempts);
                return HostUnavailable(groupTransition.Generation, unavailable.Runtime.Identity);
            }

            foreach (var item in attempts)
            {
                await ApplyCurrentPolicyAsync(item.Runtime, item.Candidate.Renderer, groupWorkToken);
            }

            using var groupLease = await group.CommitGate.EnterAsync(groupWorkToken);
            var ordered = attempts.OrderBy(static item => item.Runtime.Identity.Key, StringComparer.Ordinal).ToArray();
            var leases = new List<AsyncCommitGate.Lease>(ordered.Length);
            try
            {
                foreach (var item in ordered)
                {
                    leases.Add(await item.Runtime.CommitGate.EnterAsync(groupWorkToken));
                }

                if (!IsCurrent(group, groupTransition.Generation, attempts))
                {
                    await DisposeAllAsync(attempts);
                    return Superseded(groupTransition.Generation, null);
                }

                foreach (var item in ordered)
                {
                    await ApplyCurrentPolicyAsync(item.Runtime, item.Candidate.Renderer, groupWorkToken);
                }

                var unavailableAfterPolicy = attempts.FirstOrDefault(static item => IsUnavailable(item.Runtime));
                if (!IsCurrent(group, groupTransition.Generation, attempts) || unavailableAfterPolicy is not null)
                {
                    await DisposeAllAsync(attempts);
                    return unavailableAfterPolicy is not null
                        ? HostUnavailable(groupTransition.Generation, unavailableAfterPolicy.Runtime.Identity)
                        : Superseded(groupTransition.Generation, null);
                }

                groupWorkToken.ThrowIfCancellationRequested();
                var pendingSynchronization = CreatePendingSynchronization(groupId, attempts);
                if (pendingSynchronization is not null)
                {
                    pendingSynchronization.Clock.Start(TimeSpan.Zero);
                    foreach (var endpoint in pendingSynchronization.Endpoints)
                    {
                        endpoint.AttachResumeObserver(pendingSynchronization.Synchronizer);
                    }
                }

                var swapped = new List<GroupCandidate>();
                try
                {
                    foreach (var item in ordered)
                    {
                        item.OldRenderer = item.Runtime.ActiveRenderer;
                        swapped.Add(item);
                        await item.Candidate.Renderer.ActivateAsync(groupWorkToken);
                        if (!IsCurrent(group, groupTransition.Generation, attempts) || IsUnavailable(item.Runtime))
                        {
                            var staleDiagnostics = (await RollbackGroupAsync(swapped, attempts)).ToList();
                            if (IsUnavailable(item.Runtime))
                            {
                                staleDiagnostics.Add(new AssignmentDiagnostic(
                                    item.Runtime.Identity,
                                    AssignmentDiagnosticCode.HostUnavailable,
                                    null));
                            }
                            return new AssignmentResult(
                                groupTransition.Generation,
                                AssignmentOutcome.Superseded,
                                [],
                                false,
                                staleDiagnostics);
                        }
                    }

                    var persisted = !persistLogicalState || await PersistAsync(
                        request,
                        groupWorkToken,
                        () => IsCurrent(group, groupTransition.Generation, attempts) &&
                            attempts.All(static item => !IsUnavailable(item.Runtime)) &&
                            !callerToken.IsCancellationRequested &&
                            !shutdownToken.IsCancellationRequested);
                    if (!persisted)
                    {
                        var staleDiagnostics = (await RollbackGroupAsync(swapped, attempts)).ToList();
                        callerToken.ThrowIfCancellationRequested();
                        shutdownToken.ThrowIfCancellationRequested();
                        var unavailableAfterSave = attempts.FirstOrDefault(static item => IsUnavailable(item.Runtime));
                        if (unavailableAfterSave is not null)
                        {
                            staleDiagnostics.Add(new AssignmentDiagnostic(
                                unavailableAfterSave.Runtime.Identity,
                                AssignmentDiagnosticCode.HostUnavailable,
                                null));
                        }
                        return new AssignmentResult(
                            groupTransition.Generation,
                            AssignmentOutcome.Superseded,
                            [],
                            false,
                            staleDiagnostics);
                    }

                    foreach (var item in attempts)
                    {
                        item.Runtime.ActiveRenderer = item.Candidate.Renderer;
                        item.Candidate.Committed = true;
                        item.Runtime.InFlightCandidates.Remove(item.Generation);
                    }

                    group.SetSynchronization(
                        pendingSynchronization?.Clock,
                        pendingSynchronization?.Synchronizer,
                        _timeProvider,
                        () => SynchronizeGroupAsync(groupId, CancellationToken.None));

                    foreach (var item in attempts) MuteRetiredAudio(item.OldRenderer);
                    _audioPolicy.Apply(_state, ActiveAudioEndpoints());

                    if (pendingSynchronization is not null)
                    {
                        await pendingSynchronization.Synchronizer.SampleAsync(
                            attempts.Select(static item => item.Candidate.Renderer)
                                .OfType<IVideoPlaybackEndpoint>()
                                .ToArray(),
                            groupWorkToken);
                    }

                    foreach (var item in attempts)
                    {
                        await StopAndDisposeOldAsync(item.OldRenderer, item.Runtime.Identity);
                    }

                    return new AssignmentResult(
                        groupTransition.Generation,
                        AssignmentOutcome.Applied,
                        request.Targets.Select(static target => target.Monitor).ToArray(),
                        persistLogicalState,
                        []);
                }
                catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
                {
                    var diagnostics = await RollbackGroupAsync(swapped, attempts);
                    return new AssignmentResult(
                        groupTransition.Generation,
                        AssignmentOutcome.Superseded,
                        [],
                        false,
                        diagnostics);
                }
                catch (OperationCanceledException)
                {
                    await RollbackGroupAsync(swapped, attempts);
                    throw;
                }
                catch (Exception failure)
                {
                    var diagnostics = await RollbackGroupAsync(swapped, attempts);
                    throw ActivationFailure("Display-group assignment failed and was rolled back.", failure, diagnostics);
                }
            }
            finally
            {
                for (var index = leases.Count - 1; index >= 0; index--) leases[index].Dispose();
            }
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested && !shutdownToken.IsCancellationRequested)
        {
            await DisposeAllAsync(attempts);
            return Superseded(groupTransition.Generation, null);
        }
        catch (OperationCanceledException)
        {
            await DisposeAllAsync(attempts);
            throw;
        }
        catch (WallpaperActivationException)
        {
            throw;
        }
        catch (Exception failure)
        {
            await DisposeAllAsync(attempts);
            throw ActivationFailure("Display-group candidate preparation failed.", failure);
        }
        finally
        {
            foreach (var item in attempts) item.Runtime.InFlightCandidates.Remove(item.Generation);
            PublishSnapshot();
        }

        async Task PrepareGroupCandidateAsync(GroupCandidate item)
        {
            try
            {
                await PrepareAsync(item.Candidate, request, groupWorkToken);
            }
            catch
            {
                await groupWorkCancellation.CancelAsync();
                throw;
            }
        }
    }

    private async ValueTask SetReasonsOnDispatcherAsync(
        MonitorIdentity output,
        PerformanceReasonOwner owner,
        IReadOnlySet<PerformanceReason> reasons,
        CancellationToken callerToken,
        CancellationToken shutdownToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(callerToken, shutdownToken);
        var runtime = GetMonitor(output);
        var previousOwnedReasons = runtime.Reasons.Snapshot(owner);
        var renderers = runtime.InFlightCandidates.Values
            .Append(runtime.ActiveRenderer)
            .Where(static renderer => renderer is not null)
            .Cast<IWallpaperRenderer>()
            .Distinct()
            .ToArray();
        var rendererRequests = renderers.ToDictionary(
            static renderer => renderer,
            renderer => ToRequest(EvaluatePolicy(runtime, renderer)));
        var reasonsChanged = runtime.Reasons.ReplaceOwnedReasons(owner, reasons);
        if (!reasonsChanged) return;
        try
        {
            var snapshot = runtime.Reasons.Snapshot();
            if (snapshot.Contains(PerformanceReason.MonitorDisconnected) ||
                snapshot.Contains(PerformanceReason.Shutdown))
            {
                runtime.InvalidateTransition();
                foreach (var group in _groups.Values.Where(group => group.Members.Contains(output)))
                {
                    group.InvalidateTransition();
                }
            }

            foreach (var candidate in runtime.InFlightCandidates.Values.ToArray())
            {
                if (candidate.Lifecycle is RendererLifecycle.Ready or RendererLifecycle.Active)
                {
                    var policy = EvaluatePolicy(runtime, candidate);
                    var request = ToRequest(policy);
                    if (!Equals(rendererRequests[candidate], request))
                    {
                        await candidate.ApplyPerformanceAsync(request, linked.Token);
                    }
                }
            }

            using var lease = await runtime.CommitGate.EnterAsync(linked.Token);
            if (runtime.ActiveRenderer is { } active)
            {
                var policy = EvaluatePolicy(runtime, active);
                var request = ToRequest(policy);
                if (!Equals(rendererRequests[active], request))
                {
                    await active.ApplyPerformanceAsync(request, linked.Token);
                }
            }

            _audioPolicy.Apply(_state, ActiveAudioEndpoints());
        }
        catch
        {
            runtime.Reasons.ReplaceOwnedReasons(owner, previousOwnedReasons);
            foreach (var (renderer, previousRequest) in rendererRequests)
            {
                try
                {
                    await renderer.ApplyPerformanceAsync(previousRequest, CancellationToken.None);
                }
                catch
                {
                    // Preserve the original policy failure while keeping shutdown bounded.
                }
            }

            PublishSnapshot();
            throw;
        }

        PublishSnapshot();
    }

    private static async Task PrepareAsync(
        CandidateAttempt candidate,
        AssignmentRequest request,
        CancellationToken cancellationToken)
    {
        var descriptor = AssignmentCommit.CreateDescriptor(candidate.Target);
        var context = new RendererContext(
            candidate.Target.HostHwnd,
            descriptor,
            request.VirtualCanvas,
            candidate.Target.Viewport,
            candidate.Target.Settings,
            candidate.Target.SourceCrop);
        await candidate.Renderer.InitializeAsync(context, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        await candidate.Renderer.LoadAsync(request.Wallpaper.Source, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<bool> PersistAsync(
        AssignmentRequest request,
        CancellationToken cancellationToken,
        Func<bool> isStillCurrent)
    {
        using var lease = await _stateCommitGate.EnterAsync(cancellationToken);
        AssignmentCommit commit;
        lock (_snapshotGate)
        {
            commit = AssignmentCommit.Create(_state, request);
        }

        await _stateStore.SaveAsync(commit.Next, cancellationToken);
        if (!isStillCurrent())
        {
            await _stateStore.SaveAsync(commit.Previous, CancellationToken.None);
            return false;
        }

        lock (_snapshotGate)
        {
            _state = commit.Next;
        }

        return true;
    }

    private async Task<IReadOnlyList<AssignmentDiagnostic>> RollbackGroupAsync(
        IReadOnlyList<GroupCandidate> swapped,
        IReadOnlyList<GroupCandidate> all)
    {
        var diagnostics = new List<AssignmentDiagnostic>();
        for (var index = swapped.Count - 1; index >= 0; index--)
        {
            var item = swapped[index];
            try
            {
                if (item.OldRenderer is { } oldRenderer)
                {
                    await oldRenderer.ActivateAsync(CancellationToken.None);
                }
                else
                {
                    if (item.Candidate.Renderer.Lifecycle is RendererLifecycle.Active or RendererLifecycle.Ready)
                    {
                        await item.Candidate.Renderer.StopAsync(CancellationToken.None);
                    }
                }
            }
            catch (Exception failure)
            {
                diagnostics.Add(new AssignmentDiagnostic(
                    item.Runtime.Identity,
                    AssignmentDiagnosticCode.RollbackFailed,
                    NativeCode(failure)));
            }
        }

        await DisposeAllAsync(all);
        return diagnostics;
    }

    private static async Task RollbackSingleAsync(
        IWallpaperRenderer? oldRenderer,
        CandidateAttempt candidate)
    {
        if (oldRenderer is not null)
        {
            try
            {
                await oldRenderer.ActivateAsync(CancellationToken.None);
            }
            finally
            {
                await DisposeCandidateAsync(candidate);
            }
        }
        else
        {
            await DisposeCandidateAsync(candidate);
        }
    }

    private async Task StopAndDisposeOldAsync(IWallpaperRenderer? renderer, MonitorIdentity output)
    {
        if (renderer is null) return;
        try
        {
            await renderer.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            RecordRetiredCleanupFailure(renderer, output, "retired-stop", exception);
        }

        try
        {
            await renderer.DisposeAsync();
        }
        catch (Exception exception)
        {
            RecordRetiredCleanupFailure(renderer, output, "retired-dispose", exception);
        }
    }

    private void RecordRetiredCleanupFailure(
        IWallpaperRenderer renderer,
        MonitorIdentity output,
        string operation,
        Exception exception)
    {
        _diagnostics.Record(new VideoPlaybackEvent(
            operation,
            RendererSafeId(renderer),
            output.Key,
            "wallpaper-assignment",
            exception.GetType().Name,
            0,
            0));
    }

    private static async Task DisposeAllAsync(IEnumerable<GroupCandidate> attempts)
    {
        foreach (var item in attempts) await DisposeCandidateAsync(item.Candidate);
    }

    private static async Task DisposeCandidateAsync(CandidateAttempt candidate)
    {
        if (candidate.Committed || Interlocked.Exchange(ref candidate.Disposed, 1) != 0) return;
        await TryStopAsync(candidate.Renderer);
        await candidate.Renderer.DisposeAsync();
    }

    private static async Task TryStopAsync(IWallpaperRenderer renderer)
    {
        try
        {
            if (renderer.Lifecycle is RendererLifecycle.Active or RendererLifecycle.Ready)
            {
                await renderer.StopAsync(CancellationToken.None);
            }
        }
        catch
        {
            // Disposal is still mandatory; rollback diagnostics are recorded by the caller when relevant.
        }
    }

    private async Task ApplyCurrentPolicyAsync(
        MonitorRuntime runtime,
        IWallpaperRenderer renderer,
        CancellationToken cancellationToken)
    {
        var policy = EvaluatePolicy(runtime, renderer);
        await renderer.ApplyPerformanceAsync(ToRequest(policy), cancellationToken);
    }

    private PerformancePolicy EvaluatePolicy(MonitorRuntime runtime, IWallpaperRenderer renderer) =>
        PerformancePolicyEvaluator.Evaluate(
            runtime.Identity,
            runtime.Reasons.Snapshot(),
            _policyOptions,
            renderer.Capabilities.CanThrottlePresentation
                ? RendererThrottleCapability.Cooperative
                : RendererThrottleCapability.Unsupported);

    private static RendererPerformanceRequest ToRequest(PerformancePolicy policy) =>
        new(policy.State, policy.TargetFps);

    private MonitorRuntime GetMonitor(MonitorIdentity identity)
    {
        if (!_monitors.TryGetValue(identity.Key, out var runtime))
        {
            runtime = new MonitorRuntime(identity);
            _monitors.Add(identity.Key, runtime);
        }

        return runtime;
    }

    private void PublishSnapshot()
    {
        var snapshot = new EngineSnapshot(
            _state,
            _monitors.Values
                .OrderBy(static runtime => runtime.Identity.Key, StringComparer.Ordinal)
                .Select(runtime => new OutputRuntimeSnapshot(
                    runtime.Identity,
                    runtime.Generation,
                    runtime.ActiveRenderer?.Lifecycle,
                    runtime.ActiveRenderer?.PerformanceState,
                    null,
                    runtime.Reasons.Snapshot()))
                .ToArray());
        lock (_snapshotGate)
        {
            _publishedSnapshot = snapshot;
        }
    }

    private IReadOnlyList<IVideoAudioEndpoint> ActiveAudioEndpoints() =>
        _monitors.Values
            .Select(static runtime => runtime.ActiveRenderer)
            .OfType<IVideoAudioEndpoint>()
            .ToArray();

    private ValueTask SynchronizeGroupOnDispatcherAsync(
        DisplayGroupId groupId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_groups.TryGetValue(groupId, out var group) ||
            group.DriftSynchronizer is null || group.Members.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        var endpoints = group.Members
            .Select(member => _monitors.GetValueOrDefault(member.Key)?.ActiveRenderer)
            .OfType<IVideoPlaybackEndpoint>()
            .ToArray();
        return new ValueTask(group.DriftSynchronizer.SampleAsync(endpoints, cancellationToken));
    }

    private static void MuteRetiredAudio(IWallpaperRenderer? renderer)
    {
        if (renderer is IVideoAudioEndpoint { IsMuted: false } audio) audio.SetMuted(true);
    }

    private void SupersedeGroupsContaining(MonitorIdentity output, DisplayGroupId? except = null)
    {
        foreach (var (id, group) in _groups)
        {
            if (id != except && group.Members.Any(member => member.Key == output.Key))
            {
                group.Deactivate();
            }
        }
    }

    private static bool IsCurrent(MonitorRuntime runtime, long generation) => runtime.IsCurrent(generation);

    private static bool IsCurrent(
        DisplayGroupRuntime group,
        long generation,
        IReadOnlyList<GroupCandidate> attempts) =>
        group.IsCurrent(generation) && attempts.All(item => item.Runtime.IsCurrent(item.Generation));

    private static bool IsUnavailable(MonitorRuntime runtime)
    {
        var reasons = runtime.Reasons.Snapshot();
        return reasons.Contains(PerformanceReason.MonitorDisconnected) ||
            reasons.Contains(PerformanceReason.Shutdown);
    }

    private static string RendererSafeId(IWallpaperRenderer renderer) =>
        renderer is IVideoPlaybackEndpoint playbackEndpoint
            ? playbackEndpoint.Id
            : renderer.GetType().Name;

    private PendingGroupSynchronization? CreatePendingSynchronization(
        DisplayGroupId groupId,
        IReadOnlyList<GroupCandidate> attempts)
    {
        var endpoints = attempts.Select(static item => item.Candidate.Renderer)
            .OfType<IVideoSynchronizationEndpoint>()
            .ToArray();
        var duration = TryGetSharedVideoDuration(endpoints);
        if (duration is null)
        {
            return null;
        }

        var clock = new LoopingPlaybackClock(_timeProvider, duration.Value);
        var synchronizer = new VideoDriftSynchronizer(
            _dispatcher,
            clock,
            _timeProvider,
            resumeSample: () => SynchronizeGroupAsync(groupId, CancellationToken.None));
        return new PendingGroupSynchronization(clock, synchronizer, endpoints);
    }

    private static TimeSpan? TryGetSharedVideoDuration(IReadOnlyList<IVideoSynchronizationEndpoint> endpoints)
    {
        if (endpoints.Count == 0)
        {
            return null;
        }

        TimeSpan? sharedDuration = null;
        foreach (var endpoint in endpoints)
        {
            if (endpoint.Duration <= TimeSpan.Zero)
            {
                return null;
            }

            if (sharedDuration is null)
            {
                sharedDuration = endpoint.Duration;
                continue;
            }

            if (sharedDuration.Value != endpoint.Duration)
            {
                return null;
            }
        }

        return sharedDuration;
    }

    private static AssignmentResult Superseded(long generation, MonitorIdentity? output) =>
        new(generation, AssignmentOutcome.Superseded, [], false, []);

    private static AssignmentResult HostUnavailable(long generation, MonitorIdentity output) =>
        new(
            generation,
            AssignmentOutcome.Superseded,
            [],
            false,
            [new AssignmentDiagnostic(output, AssignmentDiagnosticCode.HostUnavailable, null)]);

    private static WallpaperActivationException ActivationFailure(
        string message,
        Exception failure,
        IReadOnlyList<AssignmentDiagnostic>? diagnostics = null) =>
        new(message, failure, diagnostics);

    private static string NativeCode(Exception failure) =>
        failure.HResult.ToString("X8", CultureInfo.InvariantCulture);

    private static void ValidateRequest(AssignmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Wallpaper);
        ArgumentNullException.ThrowIfNull(request.VirtualCanvas);
        ArgumentNullException.ThrowIfNull(request.Targets);
        if (request.Targets.Count == 0) throw new ArgumentException("At least one output target is required.", nameof(request));
        if (request.Targets.Any(static target => target is null)) throw new ArgumentException("Output targets cannot contain null.", nameof(request));
        if (request.Targets.Select(static target => target.Monitor.Key).Distinct(StringComparer.Ordinal).Count() != request.Targets.Count)
            throw new ArgumentException("Output targets must be unique.", nameof(request));
        if (request.Targets.Any(static target => target.HostHwnd == 0)) throw new ArgumentException("Every output requires a host window.", nameof(request));
        if (request.Targets.Any(static target => target.Settings.TargetFps is < 1 or > 60)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Targets.Any(static target => target.Settings.VolumePercent is < 0 or > 100)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Mode == DisplayMode.Independent && (request.Targets.Count != 1 || request.GroupId is not null))
            throw new ArgumentException("Independent assignments require exactly one target and no group ID.", nameof(request));
        if (request.Mode != DisplayMode.Independent && request.GroupId is null)
            throw new ArgumentException("Grouped assignments require a group ID.", nameof(request));
    }

    private sealed class CandidateAttempt(long generation, OutputAssignmentTarget target, IWallpaperRenderer renderer)
    {
        public long Generation { get; } = generation;
        public OutputAssignmentTarget Target { get; } = target;
        public IWallpaperRenderer Renderer { get; } = renderer;
        public bool Committed { get; set; }
        public int Disposed;
    }

    private sealed class GroupCandidate(
        MonitorRuntime runtime,
        long generation,
        CancellationToken transitionToken,
        CandidateAttempt candidate)
    {
        public MonitorRuntime Runtime { get; } = runtime;
        public long Generation { get; } = generation;
        public CancellationToken TransitionToken { get; } = transitionToken;
        public CandidateAttempt Candidate { get; } = candidate;
        public IWallpaperRenderer? OldRenderer { get; set; }
    }

    private sealed class PendingGroupSynchronization(
        LoopingPlaybackClock clock,
        VideoDriftSynchronizer synchronizer,
        IReadOnlyList<IVideoSynchronizationEndpoint> endpoints)
    {
        public LoopingPlaybackClock Clock { get; } = clock;
        public VideoDriftSynchronizer Synchronizer { get; } = synchronizer;
        public IReadOnlyList<IVideoSynchronizationEndpoint> Endpoints { get; } = endpoints;
    }
}
