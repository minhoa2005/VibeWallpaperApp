using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Sources;

public sealed class ActiveVideoSourceMonitor : IAsyncDisposable
{
    private readonly IStateStore _stateStore;
    private readonly SourceChangeMonitor _changes;
    private readonly VideoSourceRevalidator _revalidator;
    private readonly TimeSpan _interval;
    private readonly TimeSpan _perSourceTimeout;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _worker;

    public ActiveVideoSourceMonitor(
        IStateStore stateStore,
        SourceChangeMonitor changes,
        VideoSourceRevalidator revalidator,
        TimeSpan interval,
        TimeSpan perSourceTimeout,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(revalidator);
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (perSourceTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(perSourceTimeout));
        _stateStore = stateStore;
        _changes = changes;
        _revalidator = revalidator;
        _interval = interval;
        _perSourceTimeout = perSourceTimeout;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is not null) throw new InvalidOperationException("Active source monitoring has already started.");
        await _changes.StartAsync(cancellationToken).ConfigureAwait(false);
        _worker = RunAsync(_shutdown.Token);
    }

    public async Task CheckNowAsync(CancellationToken cancellationToken)
    {
        await _changes.RefreshAsync(cancellationToken).ConfigureAwait(false);
        var state = (await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        var activeVideoIds = state.Assignments
            .Select(static assignment => assignment.Wallpaper)
            .Distinct()
            .Where(id => state.Library.Any(item =>
                item.Definition.Id == id && item.Definition.Source is VideoSource))
            .ToArray();
        foreach (var id in activeVideoIds)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            bounded.CancelAfter(_perSourceTimeout);
            try
            {
                await _revalidator.RevalidateBeforeActivationAsync(id, bounded.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && bounded.IsCancellationRequested)
            {
                // One bounded source check must not prevent later active sources from being checked.
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_worker is not null)
        {
            try { await _worker.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        }
        _shutdown.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await CheckNowAsync(cancellationToken).ConfigureAwait(false);
        using var timer = new PeriodicTimer(_interval, _timeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            await CheckNowAsync(cancellationToken).ConfigureAwait(false);
    }
}
