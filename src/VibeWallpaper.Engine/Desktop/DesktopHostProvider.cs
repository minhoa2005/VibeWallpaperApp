using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Native;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Engine.Desktop;

public sealed class DesktopHostUnavailableException(string diagnostic) : InvalidOperationException(diagnostic);

public sealed class DesktopHostProvider : IDesktopHostProvider
{
    private readonly IEngineDispatcher _dispatcher;
    private readonly IDesktopHostResolver _resolver;
    private readonly IWallpaperHostWindowApi _windows;
    private readonly Dictionary<string, WallpaperHostWindow> _hosts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MonitorDescriptor> _monitors = new(StringComparer.Ordinal);
    private bool _disposed;

    public DesktopHostProvider(IEngineDispatcher dispatcher)
        : this(
            dispatcher,
            new WorkerWResolver(dispatcher, NativeDesktopWindowApi.Instance),
            NativeDesktopWindowApi.Instance)
    {
    }

    internal DesktopHostProvider(
        IEngineDispatcher dispatcher,
        IDesktopHostResolver resolver,
        IWallpaperHostWindowApi windows)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(windows);
        _dispatcher = dispatcher;
        _resolver = resolver;
        _windows = windows;
    }

    public Task<IWallpaperHostWindow> CreateAsync(
        MonitorDescriptor monitor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        return _dispatcher.InvokeAsync(
            async token => (IWallpaperHostWindow)await CreateOnEngineThreadAsync(monitor, token),
            cancellationToken);
    }

    public Task ReattachAllAsync(CancellationToken cancellationToken) =>
        _dispatcher.InvokeAsync(async token =>
        {
            ThrowIfDisposed();
            foreach (var monitor in _monitors.Values.ToArray())
            {
                token.ThrowIfCancellationRequested();
                _ = await CreateOnEngineThreadAsync(monitor, token);
            }
        }, cancellationToken);

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(_dispatcher.InvokeAsync(async _ =>
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            foreach (var host in _hosts.Values)
            {
                await host.DisposeAsync();
            }

            _hosts.Clear();
            _monitors.Clear();
        }));
    }

    private async ValueTask<WallpaperHostWindow> CreateOnEngineThreadAsync(
        MonitorDescriptor monitor,
        CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        var key = monitor.Identity.Key;
        _monitors[key] = monitor;
        if (_hosts.TryGetValue(key, out var existing))
        {
            if (existing.IsAttached)
            {
                if (existing.Bounds != monitor.Bounds)
                {
                    existing.SetBounds(monitor.Bounds);
                }

                return existing;
            }

            await existing.DisposeAsync();
            _hosts.Remove(key);
        }

        var resolution = _resolver.Resolve();
        if (resolution.IsDegraded || resolution.ParentHwnd == 0 || !_windows.IsWindow(resolution.ParentHwnd))
        {
            throw new DesktopHostUnavailableException(
                resolution.Diagnostic ?? $"Desktop host strategy {resolution.Strategy} did not return a valid HWND.");
        }

        var created = new WallpaperHostWindow(_dispatcher, _windows, resolution, monitor);
        _hosts.Add(key, created);
        created.Show();
        return created;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
