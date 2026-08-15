using System.Collections.ObjectModel;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Runtime;

public interface IRuntimeWallpaperActivator
{
    Task ActivateAsync(
        MonitorIdentity output,
        WallpaperDefinition wallpaper,
        WallpaperAssignment persistedAssignment,
        long generation,
        CancellationToken cancellationToken);
}

public sealed class FallbackActivationException : Exception
{
    public FallbackActivationException(MonitorIdentity output, string reasonCode, Exception innerException)
        : base($"Solid fallback activation failed for output '{output.Key}' ({reasonCode}).", innerException)
    {
        Output = output;
        ReasonCode = reasonCode;
    }

    public MonitorIdentity Output { get; }
    public string ReasonCode { get; }
}

public sealed record FallbackDiagnostic(
    MonitorIdentity Output,
    string Code,
    string ReasonCode,
    string Message,
    Exception? Exception = null);

public sealed record FallbackInitializationResult(IReadOnlyList<FallbackDiagnostic> Diagnostics);

public sealed class FallbackRendererCoordinator
{
    private static readonly WallpaperId BuiltInFallbackId = new(new Guid("00000000-0000-0000-0000-00000000F001"));
    private PersistedState _persistedState;
    private readonly AppSettings _settings;
    private readonly IRuntimeWallpaperActivator _activator;
    private readonly Dictionary<string, EffectiveWallpaperState> _effective = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _generations = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _transitions = new(1, 1);
    private readonly object _snapshotGate = new();

    public FallbackRendererCoordinator(
        PersistedState persistedState,
        AppSettings settings,
        IRuntimeWallpaperActivator activator)
    {
        ArgumentNullException.ThrowIfNull(persistedState);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(activator);
        _persistedState = persistedState;
        _settings = settings;
        _activator = activator;

        foreach (var assignment in persistedState.Assignments)
        {
            _effective[assignment.Monitor.Identity.Key] = new EffectiveWallpaperState(
                assignment.Wallpaper,
                EffectiveWallpaperKind.Assigned,
                assignment.Wallpaper,
                null);
            _generations[assignment.Monitor.Identity.Key] = 0;
        }
    }

    public async Task<FallbackInitializationResult> InitializeAsync(
        Func<WallpaperKind, bool> rendererAvailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(rendererAvailable);
        var diagnostics = new List<FallbackDiagnostic>();
        foreach (var assignment in _persistedState.Assignments)
        {
            var libraryItem = FindLibraryItem(assignment.Wallpaper);
            var status = libraryItem?.Validation.Status ?? SourceValidationStatus.Missing;
            var available = libraryItem is not null && rendererAvailable(libraryItem.Definition.Source.Kind);
            try
            {
                await ReconcileAsync(assignment.Monitor.Identity, status, available, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (FallbackActivationException exception)
            {
                diagnostics.Add(new FallbackDiagnostic(
                    assignment.Monitor.Identity,
                    "wallpaper.fallback.activation_failed",
                    exception.ReasonCode,
                    "Không thể kích hoạt wallpaper dự phòng trên một màn hình. Ứng dụng vẫn tiếp tục chạy.",
                    exception));
            }
            catch (ArgumentException exception) when (
                string.Equals(exception.ParamName, "selectedOutputs", StringComparison.Ordinal))
            {
                diagnostics.Add(new FallbackDiagnostic(
                    assignment.Monitor.Identity,
                    "wallpaper.restore.skipped",
                    "wallpaper.output.disconnected",
                    "Không thể khôi phục wallpaper trên một màn hình đã ngắt kết nối. Ứng dụng vẫn tiếp tục chạy.",
                    exception));
            }
            catch (Exception exception)
            {
                diagnostics.Add(new FallbackDiagnostic(
                    assignment.Monitor.Identity,
                    "wallpaper.restore.failed",
                    "wallpaper.restore.unexpected",
                    "Không thể khôi phục một wallpaper đã lưu. Ứng dụng vẫn tiếp tục chạy.",
                    exception));
            }
        }

        return new FallbackInitializationResult(diagnostics.AsReadOnly());
    }

    public void UpdatePersistedState(PersistedState persistedState)
    {
        ArgumentNullException.ThrowIfNull(persistedState);
        lock (_snapshotGate)
        {
            _persistedState = persistedState;
            foreach (var assignment in persistedState.Assignments)
            {
                if (!_effective.ContainsKey(assignment.Monitor.Identity.Key) ||
                    _effective[assignment.Monitor.Identity.Key].AssignedWallpaper != assignment.Wallpaper)
                {
                    _effective[assignment.Monitor.Identity.Key] = new EffectiveWallpaperState(
                        assignment.Wallpaper,
                        EffectiveWallpaperKind.Assigned,
                        assignment.Wallpaper,
                        null);
                }

                _generations.TryAdd(assignment.Monitor.Identity.Key, 0);
            }
        }
    }

    public async Task ReconcileAsync(
        MonitorIdentity output,
        SourceValidationStatus sourceStatus,
        bool rendererAvailable,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (!Enum.IsDefined(sourceStatus))
        {
            throw new ArgumentException("A defined source status is required.", nameof(sourceStatus));
        }

        await _transitions.WaitAsync(cancellationToken);
        try
        {
            var assignment = _persistedState.Assignments.FirstOrDefault(
                item => string.Equals(item.Monitor.Identity.Key, output.Key, StringComparison.Ordinal));
            if (assignment is null)
            {
                throw new KeyNotFoundException($"Output '{output.Key}' has no persisted wallpaper assignment.");
            }

            var assignedItem = FindLibraryItem(assignment.Wallpaper);
            var reason = ReasonFor(sourceStatus, assignedItem is not null, rendererAvailable);
            var wallpaper = reason is null ? assignedItem!.Definition : CreateSolidFallback();
            long generation;
            lock (_snapshotGate)
            {
                generation = _generations[output.Key] + 1;
                _generations[output.Key] = generation;
            }

            try
            {
                await _activator.ActivateAsync(output, wallpaper, assignment, generation, cancellationToken);
            }
            catch (Exception exception) when (reason is not null && exception is not OperationCanceledException)
            {
                throw new FallbackActivationException(output, reason, exception);
            }

            var next = new EffectiveWallpaperState(
                assignment.Wallpaper,
                reason is null ? EffectiveWallpaperKind.Assigned : EffectiveWallpaperKind.SolidFallback,
                wallpaper.Id,
                reason);
            lock (_snapshotGate)
            {
                _effective[output.Key] = next;
            }
        }
        finally
        {
            _transitions.Release();
        }
    }

    public EffectiveWallpaperState GetEffectiveState(MonitorIdentity output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_snapshotGate)
        {
            return _effective.TryGetValue(output.Key, out var state)
                ? state
                : throw new KeyNotFoundException($"Output '{output.Key}' has no effective wallpaper state.");
        }
    }

    public long GetGeneration(MonitorIdentity output)
    {
        ArgumentNullException.ThrowIfNull(output);
        lock (_snapshotGate)
        {
            return _generations.TryGetValue(output.Key, out var generation)
                ? generation
                : throw new KeyNotFoundException($"Output '{output.Key}' has no runtime generation.");
        }
    }

    public IReadOnlyDictionary<string, EffectiveWallpaperState> GetSnapshot()
    {
        lock (_snapshotGate)
        {
            return new ReadOnlyDictionary<string, EffectiveWallpaperState>(
                new Dictionary<string, EffectiveWallpaperState>(_effective, StringComparer.Ordinal));
        }
    }

    private WallpaperLibraryItem? FindLibraryItem(WallpaperId id) =>
        _persistedState.Library.FirstOrDefault(item => item.Definition.Id == id);

    private WallpaperDefinition CreateSolidFallback()
    {
        if (_settings.FallbackWallpaper is { } configured)
        {
            var configuredItem = FindLibraryItem(configured);
            if (configuredItem?.Definition.Source is SolidColorSource)
            {
                return configuredItem.Definition;
            }
        }

        return new WallpaperDefinition(
            _settings.FallbackWallpaper ?? BuiltInFallbackId,
            "Solid fallback",
            SolidColorSource.Create(_settings.FallbackColor),
            _settings.DefaultFit,
            _settings.DefaultTargetFps,
            false,
            false,
            0,
            false);
    }

    private static string? ReasonFor(
        SourceValidationStatus status,
        bool definitionFound,
        bool rendererAvailable)
    {
        if (!definitionFound || status == SourceValidationStatus.Missing)
        {
            return "wallpaper.source.missing";
        }

        if (status == SourceValidationStatus.Invalid)
        {
            return "wallpaper.source.invalid";
        }

        if (status == SourceValidationStatus.Unsupported)
        {
            return "wallpaper.source.unsupported";
        }

        return rendererAvailable ? null : "wallpaper.renderer.unavailable";
    }
}
