#nullable enable
using System.Diagnostics;

namespace VibeWallpaper.App.Coordination;

public enum ApplicationStageKind
{
    PerMonitorV2,
    SingleInstance,
    LoggingConfigurationState,
    EngineDispatcher,
    TopologyAndDesktopHosts,
    RestoreAssignments,
    ActivityObservers,
    TrayAndUi,
}

public interface IApplicationStage : IAsyncDisposable
{
    ApplicationStageKind Kind { get; }
    Task StartAsync(CancellationToken cancellationToken);
}

public sealed class ApplicationCoordinator : IAsyncDisposable
{
    private static readonly ApplicationStageKind[] RequiredOrder = Enum.GetValues<ApplicationStageKind>();
    private readonly IReadOnlyList<IApplicationStage> _stages;
    private readonly TimeSpan _shutdownDeadline;
    private readonly TimeSpan _shutdownStepTimeout;
    private readonly List<IApplicationStage> _started = [];
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Task? _stopTask;
    private bool _startAttempted;

    public ApplicationCoordinator(
        IEnumerable<IApplicationStage> stages,
        TimeSpan shutdownDeadline,
        TimeSpan? shutdownStepTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(stages);
        if (shutdownDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownDeadline));
        }

        var stepTimeout = shutdownStepTimeout ?? TimeSpan.FromSeconds(3);
        if (stepTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownStepTimeout));
        }

        var byKind = stages.ToDictionary(static stage => stage.Kind);
        var missing = RequiredOrder.Where(kind => !byKind.ContainsKey(kind)).ToArray();
        if (missing.Length != 0)
        {
            throw new ArgumentException($"Missing application stages: {string.Join(", ", missing)}.", nameof(stages));
        }

        _stages = RequiredOrder.Select(kind => byKind[kind]).ToArray();
        _shutdownDeadline = shutdownDeadline;
        _shutdownStepTimeout = stepTimeout;
    }

    public bool ShutdownTimedOut { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_startAttempted)
            {
                throw new InvalidOperationException("Application startup can only be attempted once.");
            }

            _startAttempted = true;
            try
            {
                foreach (var stage in _stages)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _started.Add(stage);
                    await stage.StartAsync(cancellationToken);
                }
            }
            catch
            {
                await StopStartedAsync(_shutdownDeadline);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task StopAsync()
    {
        lock (_started)
        {
            return _stopTask ??= StopOnceAsync();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _lifecycle.Dispose();
    }

    private async Task StopOnceAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await StopStartedAsync(_shutdownDeadline);
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    private async Task StopStartedAsync(TimeSpan deadline)
    {
        var startedAt = Stopwatch.GetTimestamp();
        for (var index = _started.Count - 1; index >= 0; index--)
        {
            var stage = _started[index];
            var disposal = Task.Run(async () => await stage.DisposeAsync());

            var elapsed = Stopwatch.GetElapsedTime(startedAt);
            var remaining = deadline - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                ShutdownTimedOut = true;
                continue;
            }

            try
            {
                var stepRemaining = remaining < _shutdownStepTimeout ? remaining : _shutdownStepTimeout;
                await disposal.WaitAsync(stepRemaining);
            }
            catch (TimeoutException)
            {
                ShutdownTimedOut = true;
            }
            catch
            {
                // Shutdown is best-effort; later stages must still be given a chance to release.
            }
        }

        _started.Clear();
    }
}
