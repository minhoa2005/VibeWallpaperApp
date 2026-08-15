using System.Diagnostics;

namespace VibeWallpaper.Engine.Runtime.Recovery;

public sealed class ShutdownCoordinator
{
    private readonly IReadOnlyList<IShutdownStep> _steps;
    private readonly IProcessTerminator _terminator;
    private readonly RecoveryPolicy _policy;
    private readonly object _gate = new();
    private Task<ShutdownResult>? _shutdownTask;
    private bool _acceptingWork = true;

    public ShutdownCoordinator(
        IEnumerable<IShutdownStep> steps,
        IProcessTerminator terminator,
        RecoveryPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(steps);
        _steps = steps.ToArray();
        if (_steps.Any(static step => step is null)) throw new ArgumentException("Shutdown steps cannot contain null.", nameof(steps));
        _terminator = terminator ?? throw new ArgumentNullException(nameof(terminator));
        _policy = policy ?? RecoveryPolicy.Default;
    }

    public bool TryBeginWork()
    {
        lock (_gate) return _acceptingWork;
    }

    public Task<ShutdownResult> ShutdownAsync()
    {
        lock (_gate)
        {
            _acceptingWork = false;
            return _shutdownTask ??= RunShutdownAsync();
        }
    }

    private async Task<ShutdownResult> RunShutdownAsync()
    {
        var startedAt = Stopwatch.GetTimestamp();
        var timedOutSteps = new List<string>();
        foreach (var step in _steps)
        {
            var remaining = _policy.ShutdownTotalTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining <= TimeSpan.Zero)
            {
                timedOutSteps.Add(step.Name);
                continue;
            }

            var timeout = remaining < _policy.ShutdownStepTimeout ? remaining : _policy.ShutdownStepTimeout;
            try
            {
                await step.ExecuteAsync(CancellationToken.None).AsTask().WaitAsync(timeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                timedOutSteps.Add(step.Name);
            }
            catch
            {
                // Shutdown is best effort; later steps still run.
            }
        }

        if (timedOutSteps.Count != 0)
        {
            var remaining = _policy.ShutdownTotalTimeout - Stopwatch.GetElapsedTime(startedAt);
            if (remaining > TimeSpan.Zero)
                await Task.Delay(remaining).ConfigureAwait(false);
            _terminator.TerminateCurrentProcess(-1);
        }

        return new(timedOutSteps.Count != 0, timedOutSteps.AsReadOnly());
    }
}
