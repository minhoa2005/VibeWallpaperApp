using VibeWallpaper.Engine.Sources;
using VibeWallpaper.Engine.Activity;

namespace VibeWallpaper.App.Coordination;

public sealed record SourceMonitoringServices(
    SourceChangeMonitor Changes,
    ActiveVideoSourceMonitor Active);

public sealed class SourceMonitoringStage(
    Func<SourceMonitoringServices> servicesFactory) : IApplicationStage
{
    private SourceMonitoringServices? _services;

    public ApplicationStageKind Kind => ApplicationStageKind.ActivityObservers;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_services is not null) throw new InvalidOperationException("Source monitoring has already started.");
        var services = servicesFactory();
        _services = services;
        try
        {
            await services.Active.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await DisposeServicesAsync(services).ConfigureAwait(false);
            _services = null;
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_services is not { } services) return;
        _services = null;
        await DisposeServicesAsync(services).ConfigureAwait(false);
    }

    private static async ValueTask DisposeServicesAsync(SourceMonitoringServices services)
    {
        await services.Active.DisposeAsync().ConfigureAwait(false);
        await services.Changes.DisposeAsync().ConfigureAwait(false);
    }
}

public sealed class ActivityObserversStage : IApplicationStage
{
    private readonly Func<ActivityObservationServices> _activityFactory;
    private readonly Func<SourceMonitoringServices>? _sourceFactory;
    private ActivityObservationServices? _activity;
    private SourceMonitoringServices? _sources;

    public ActivityObserversStage(
        Func<ActivityObservationServices> activityFactory,
        Func<SourceMonitoringServices>? sourceFactory = null)
    {
        ArgumentNullException.ThrowIfNull(activityFactory);
        _activityFactory = activityFactory;
        _sourceFactory = sourceFactory;
    }

    public ApplicationStageKind Kind => ApplicationStageKind.ActivityObservers;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_activity is not null) throw new InvalidOperationException("Activity observers have already started.");
        var activity = _activityFactory();
        _activity = activity;
        try
        {
            activity.Start();
            if (_sourceFactory is not null)
            {
                var sources = _sourceFactory();
                _sources = sources;
                await sources.Active.StartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch
        {
            await DisposeOwnedAsync().ConfigureAwait(false);
            throw;
        }
    }

    public ValueTask DisposeAsync() => DisposeOwnedAsync();

    private async ValueTask DisposeOwnedAsync()
    {
        if (_sources is { } sources)
        {
            _sources = null;
            await sources.Active.DisposeAsync().ConfigureAwait(false);
            await sources.Changes.DisposeAsync().ConfigureAwait(false);
        }

        if (_activity is { } activity)
        {
            _activity = null;
            await activity.DisposeAsync().ConfigureAwait(false);
        }
    }
}
