using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Engine.Desktop;

internal interface IWallpaperHostWindowApi
{
    bool IsWindow(nint hwnd);
    DisplayViewport GetWindowBounds(nint hwnd);
    (int X, int Y) ScreenToClient(nint parentHwnd, int screenX, int screenY);
    nint CreateHostWindow(nint parentHwnd, DisplayViewport relativeBounds);
    void MoveWindow(nint hwnd, DisplayViewport relativeBounds);
    void SetRendererParent(nint rendererHwnd, nint hostHwnd);
    void ConfigureOpaqueLayeredWindow(nint hwnd, nint insertAfter);
    void SetWindowVisible(nint hwnd, bool visible);
    void DestroyWindow(nint hwnd);
}

internal sealed class WallpaperHostWindow : IWallpaperHostWindow
{
    private readonly IEngineDispatcher _dispatcher;
    private readonly IWallpaperHostWindowApi _windows;
    private readonly nint _parentHwnd;
    private bool _disposed;
    private nint _rendererHwnd;

    internal WallpaperHostWindow(
        IEngineDispatcher dispatcher,
        IWallpaperHostWindowApi windows,
        DesktopHostResolution desktopResolution,
        MonitorDescriptor monitor)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(monitor);
        _dispatcher = dispatcher;
        _windows = windows;
        ArgumentNullException.ThrowIfNull(desktopResolution);
        _parentHwnd = desktopResolution.ParentHwnd;
        DesktopResolution = desktopResolution;
        Monitor = monitor.Identity;
        Bounds = monitor.Bounds;
        AssertEngineThread();
        Hwnd = windows.CreateHostWindow(_parentHwnd, ToParentRelative(monitor.Bounds));
        if (desktopResolution.RequiresLayeredChildren)
        {
            try
            {
                windows.ConfigureOpaqueLayeredWindow(Hwnd, desktopResolution.ShellViewHwnd);
            }
            catch
            {
                var hwnd = Hwnd;
                Hwnd = 0;
                if (windows.IsWindow(hwnd))
                {
                    windows.DestroyWindow(hwnd);
                }

                throw;
            }
        }
    }

    public nint Hwnd { get; private set; }
    public MonitorIdentity Monitor { get; }
    public DisplayViewport Bounds { get; private set; }
    public bool IsVisible { get; private set; }
    internal nint ParentHwnd => _parentHwnd;
    internal DesktopHostResolution DesktopResolution { get; }
    internal bool IsAttached => !_disposed && _windows.IsWindow(Hwnd) && _windows.IsWindow(_parentHwnd);

    public void SetBounds(DisplayViewport bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);
        AssertMutable();
        _windows.MoveWindow(Hwnd, ToParentRelative(bounds));
        Bounds = bounds;
        if (_rendererHwnd != 0)
        {
            _windows.SetRendererParent(_rendererHwnd, Hwnd);
        }
    }

    public void SetRendererChild(nint rendererHwnd)
    {
        if (rendererHwnd == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(rendererHwnd));
        }

        AssertMutable();
        if (DesktopResolution.RequiresLayeredChildren)
        {
            _windows.ConfigureOpaqueLayeredWindow(rendererHwnd, 0);
        }

        _windows.SetRendererParent(rendererHwnd, Hwnd);
        _rendererHwnd = rendererHwnd;
    }

    public void Show()
    {
        AssertMutable();
        if (IsVisible)
        {
            return;
        }

        _windows.SetWindowVisible(Hwnd, true);
        IsVisible = true;
    }

    public void Hide()
    {
        AssertMutable();
        if (!IsVisible)
        {
            return;
        }

        _windows.SetWindowVisible(Hwnd, false);
        IsVisible = false;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
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
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        IsVisible = false;
        var hwnd = Hwnd;
        Hwnd = 0;
        if (_windows.IsWindow(hwnd))
        {
            _windows.DestroyWindow(hwnd);
        }

        return ValueTask.CompletedTask;
    }

    private DisplayViewport ToParentRelative(DisplayViewport absoluteBounds)
    {
        if (!_windows.IsWindow(_parentHwnd))
        {
            throw new InvalidOperationException($"Desktop parent HWND 0x{_parentHwnd:X} is no longer valid.");
        }

        var clientOrigin = _windows.ScreenToClient(_parentHwnd, absoluteBounds.X, absoluteBounds.Y);
        return new DisplayViewport(
            clientOrigin.X,
            clientOrigin.Y,
            absoluteBounds.Width,
            absoluteBounds.Height);
    }

    private void AssertMutable()
    {
        AssertEngineThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!IsAttached)
        {
            throw new InvalidOperationException("The wallpaper host or its desktop parent is no longer valid.");
        }
    }

    private void AssertEngineThread()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("Wallpaper HWND operations must run on the engine thread.");
        }
    }
}
