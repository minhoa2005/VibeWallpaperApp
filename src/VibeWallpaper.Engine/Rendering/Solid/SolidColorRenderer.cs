using System.Globalization;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Engine.Rendering.Solid;

internal interface ISolidRendererWindowApi
{
    nint CreateRendererWindow(nint parentHwnd);
    void SetColor(nint hwnd, uint color);
    void Invalidate(nint hwnd);
    void SetVisible(nint hwnd, bool visible);
    void DestroyWindow(nint hwnd);
}

internal sealed class SolidColorRenderer : IWallpaperRenderer
{
    private readonly IEngineDispatcher _dispatcher;
    private readonly ISolidRendererWindowApi _windows;
    private readonly RendererStateMachine _state = new();
    private bool _visible;

    internal SolidColorRenderer(IEngineDispatcher dispatcher, ISolidRendererWindowApi windows)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(windows);
        _dispatcher = dispatcher;
        _windows = windows;
    }

    public RendererLifecycle Lifecycle => _state.Lifecycle;
    public PerformanceState PerformanceState => _state.PerformanceState;
    public RendererCapabilities Capabilities => RendererCapabilities.Solid;
    internal nint Hwnd { get; private set; }

    public Task InitializeAsync(RendererContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        _state.TransitionTo(RendererLifecycle.Initializing);
        try
        {
            Hwnd = _windows.CreateRendererWindow(context.HostHwnd);
            return Task.CompletedTask;
        }
        catch
        {
            _state.TransitionTo(RendererLifecycle.Faulted);
            throw;
        }
    }

    public Task LoadAsync(WallpaperSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (source is not SolidColorSource solid)
        {
            throw new ArgumentException("The solid-color renderer requires a solid-color source.", nameof(source));
        }

        _state.TransitionTo(RendererLifecycle.Loading);
        try
        {
            var rgb = uint.Parse(solid.HexColor.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            var colorRef = ((rgb & 0x0000FFu) << 16) | (rgb & 0x00FF00u) | ((rgb & 0xFF0000u) >> 16);
            _windows.SetColor(Hwnd, colorRef);
            _windows.Invalidate(Hwnd);
            _state.TransitionTo(RendererLifecycle.Ready);
            return Task.CompletedTask;
        }
        catch
        {
            _state.TransitionTo(RendererLifecycle.Faulted);
            throw;
        }
    }

    public Task ActivateAsync(CancellationToken cancellationToken)
    {
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (Lifecycle == RendererLifecycle.Ready)
        {
            _state.TransitionTo(RendererLifecycle.Active);
        }
        else if (Lifecycle != RendererLifecycle.Active)
        {
            throw new InvalidOperationException($"Cannot activate a solid renderer in state {Lifecycle}.");
        }

        if (PerformanceState != PerformanceState.Suspended)
        {
            SetVisible(true);
        }

        return Task.CompletedTask;
    }

    public Task ApplyPerformanceAsync(RendererPerformanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (PerformanceState == request.State)
        {
            return Task.CompletedTask;
        }

        _state.SetPerformanceState(request.State);
        if (request.State == PerformanceState.Suspended)
        {
            SetVisible(false);
        }
        else if (Lifecycle == RendererLifecycle.Active)
        {
            SetVisible(true);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        AssertEngineThread();
        cancellationToken.ThrowIfCancellationRequested();
        if (Lifecycle is RendererLifecycle.Stopped or RendererLifecycle.Disposed)
        {
            return Task.CompletedTask;
        }

        SetVisible(false);
        _state.Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Lifecycle == RendererLifecycle.Disposed)
        {
            return ValueTask.CompletedTask;
        }

        return _dispatcher.HasThreadAccess
            ? DisposeOnEngineThread()
            : new ValueTask(_dispatcher.InvokeAsync(_ => DisposeOnEngineThread()));
    }

    private ValueTask DisposeOnEngineThread()
    {
        AssertEngineThread();
        if (Lifecycle == RendererLifecycle.Disposed)
        {
            return ValueTask.CompletedTask;
        }

        var hwnd = Hwnd;
        Hwnd = 0;
        _visible = false;
        try
        {
            if (hwnd != 0)
            {
                _windows.DestroyWindow(hwnd);
            }
        }
        finally
        {
            _state.Dispose();
        }

        return ValueTask.CompletedTask;
    }

    private void SetVisible(bool visible)
    {
        if (_visible == visible)
        {
            return;
        }

        _windows.SetVisible(Hwnd, visible);
        _visible = visible;
    }

    private void AssertEngineThread()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("Solid renderer HWND operations must run on the engine thread.");
        }
    }
}
