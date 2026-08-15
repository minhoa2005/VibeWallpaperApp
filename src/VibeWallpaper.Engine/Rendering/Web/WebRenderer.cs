using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Rendering.Web;

public sealed class WebRenderer : IWallpaperRenderer
{
    private readonly IWebControllerAdapter _adapter;
    private readonly RendererStateMachine _state = new();
    private RendererPerformanceRequest _currentRequest = new(PerformanceState.Running);
    private bool _visible;

    public WebRenderer(IWebControllerAdapter adapter)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
    }

    public RendererLifecycle Lifecycle => _state.Lifecycle;
    public PerformanceState PerformanceState => _state.PerformanceState;
    public RendererCapabilities Capabilities => RendererCapabilities.Web;

    public async Task InitializeAsync(RendererContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        _state.TransitionTo(RendererLifecycle.Initializing);
        try { await _adapter.InitializeAsync(context, cancellationToken).ConfigureAwait(false); }
        catch { _state.TransitionTo(RendererLifecycle.Faulted); throw; }
    }

    public async Task LoadAsync(WallpaperSource source, CancellationToken cancellationToken)
    {
        if (source is not WebSource web) throw new ArgumentException("The web renderer requires a web source.", nameof(source));
        cancellationToken.ThrowIfCancellationRequested();
        _state.TransitionTo(RendererLifecycle.Loading);
        try
        {
            await _adapter.NavigateAsync(web, cancellationToken).ConfigureAwait(false);
            _state.TransitionTo(RendererLifecycle.Ready);
        }
        catch { _state.TransitionTo(RendererLifecycle.Faulted); throw; }
    }

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Lifecycle == RendererLifecycle.Ready) _state.TransitionTo(RendererLifecycle.Active);
        else if (Lifecycle != RendererLifecycle.Active) throw new InvalidOperationException($"Cannot activate a web renderer in state {Lifecycle}.");
        if (PerformanceState == PerformanceState.Suspended)
        {
            return;
        }

        await _adapter.SetPresentationThrottleAsync(_currentRequest.TargetPresentationFps, cancellationToken).ConfigureAwait(false);
        await SetVisibleAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task ApplyPerformanceAsync(RendererPerformanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (_currentRequest == request) return;
        var previous = _currentRequest;
        _currentRequest = request;
        _state.SetPerformanceState(request.State);
        if (request.State == PerformanceState.Suspended)
        {
            await _adapter.SetPresentationThrottleAsync(null, cancellationToken).ConfigureAwait(false);
            await SetVisibleAsync(false, cancellationToken).ConfigureAwait(false);
            _ = await _adapter.TrySuspendAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (Lifecycle != RendererLifecycle.Active)
        {
            return;
        }

        if (previous.State == PerformanceState.Suspended)
        {
            await _adapter.ResumeAsync(cancellationToken).ConfigureAwait(false);
        }

        await _adapter.SetPresentationThrottleAsync(request.TargetPresentationFps, cancellationToken).ConfigureAwait(false);
        await SetVisibleAsync(true, cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Lifecycle is RendererLifecycle.Stopped or RendererLifecycle.Disposed) return;
        await SetVisibleAsync(false, cancellationToken).ConfigureAwait(false);
        _state.Stop();
    }

    public async ValueTask DisposeAsync()
    {
        if (Lifecycle == RendererLifecycle.Disposed) return;
        try { await _adapter.DisposeAsync().ConfigureAwait(false); }
        finally { _state.Dispose(); }
    }

    private async Task SetVisibleAsync(bool visible, CancellationToken cancellationToken)
    {
        if (_visible == visible) return;
        await _adapter.SetVisibleAsync(visible, cancellationToken).ConfigureAwait(false);
        _visible = visible;
    }
}
