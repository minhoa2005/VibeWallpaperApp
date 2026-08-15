using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Desktop;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Desktop;

public sealed class WallpaperHostWindowTests
{
    [Fact]
    public async Task CreateAsync_ShowsHostSoRendererChildrenCanReachDesktop()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi();
        windows.SetBounds(100, new DisplayViewport(0, 0, 1920, 1080));
        var provider = new DesktopHostProvider(
            dispatcher,
            new FakeDesktopHostResolver(new DesktopHostResolution(100, "WorkerWSibling", false, null)),
            windows);

        var host = await provider.CreateAsync(
            Monitor("DISPLAY-A", new DisplayViewport(0, 0, 1920, 1080)),
            TestContext.Current.CancellationToken);

        Assert.True(windows.IsVisible(host.Hwnd));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_WhenCachedParentWasDestroyed_RecreatesHostForSameMonitor()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi();
        windows.SetBounds(100, new DisplayViewport(0, 0, 1920, 1080));
        var resolver = new FakeDesktopHostResolver(new DesktopHostResolution(100, "WorkerWSibling", false, null));
        var provider = new DesktopHostProvider(dispatcher, resolver, windows);
        var monitor = Monitor("DISPLAY-A", new DisplayViewport(-1920, 0, 1920, 1080));

        var first = await provider.CreateAsync(monitor, TestContext.Current.CancellationToken);
        windows.DestroyExternally(100);
        windows.SetBounds(200, new DisplayViewport(0, 0, 1920, 1080));
        resolver.Resolution = new DesktopHostResolution(200, "WorkerWSibling", false, null);
        var second = await provider.CreateAsync(monitor, TestContext.Current.CancellationToken);

        Assert.NotEqual(first.Hwnd, second.Hwnd);
        Assert.False(windows.IsWindow(first.Hwnd));
        Assert.True(windows.IsWindow(second.Hwnd));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_UsesPhysicalBoundsRelativeToDesktopParent()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi();
        windows.SetBounds(100, new DisplayViewport(-1920, 0, 3840, 1080));
        var provider = new DesktopHostProvider(
            dispatcher,
            new FakeDesktopHostResolver(new DesktopHostResolution(100, "WorkerWSibling", false, null)),
            windows);
        var monitor = Monitor("DISPLAY-B", new DisplayViewport(0, 0, 1920, 1080));

        var host = await provider.CreateAsync(monitor, TestContext.Current.CancellationToken);

        Assert.Equal(new DisplayViewport(1920, 0, 1920, 1080), windows.Bounds(host.Hwnd));
        Assert.Equal(monitor.Bounds, host.Bounds);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task Show_WhenCalledOutsideEngineThread_RejectsHwndMutation()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi();
        windows.SetBounds(100, new DisplayViewport(0, 0, 1920, 1080));
        var provider = new DesktopHostProvider(
            dispatcher,
            new FakeDesktopHostResolver(new DesktopHostResolution(100, "WorkerWSibling", false, null)),
            windows);
        var host = await provider.CreateAsync(
            Monitor("DISPLAY-A", new DisplayViewport(0, 0, 1920, 1080)),
            TestContext.Current.CancellationToken);

        var exception = Assert.Throws<InvalidOperationException>(host.Show);

        Assert.Contains("engine thread", exception.Message, StringComparison.OrdinalIgnoreCase);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_WhenTopologyResizesReusedHost_ResizesRendererChildToFullClient()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi();
        windows.SetBounds(100, new DisplayViewport(0, 0, 3840, 2160));
        windows.AddChildWindow(3000);
        var provider = new DesktopHostProvider(
            dispatcher,
            new FakeDesktopHostResolver(new DesktopHostResolution(100, "WorkerWSibling", false, null)),
            windows);
        var host = await provider.CreateAsync(
            Monitor("DISPLAY-A", new DisplayViewport(0, 0, 1920, 1080)),
            TestContext.Current.CancellationToken);
        await dispatcher.InvokeAsync(_ =>
        {
            host.SetRendererChild(3000);
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        var reused = await provider.CreateAsync(
            Monitor("DISPLAY-A", new DisplayViewport(0, 0, 2560, 1440)),
            TestContext.Current.CancellationToken);

        Assert.Same(host, reused);
        Assert.Equal(new DisplayViewport(0, 0, 2560, 1440), windows.Bounds(3000));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_MapsPhysicalScreenOriginIntoParentClientCoordinates()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi
        {
            ClientOriginAdjustment = new PointAdjustment(-8, -30),
        };
        windows.SetBounds(100, new DisplayViewport(-1920, 0, 3840, 1080));
        var provider = new DesktopHostProvider(
            dispatcher,
            new FakeDesktopHostResolver(new DesktopHostResolution(100, "WorkerWSibling", false, null)),
            windows);

        var host = await provider.CreateAsync(
            Monitor("DISPLAY-B", new DisplayViewport(0, 0, 1920, 1080)),
            TestContext.Current.CancellationToken);

        Assert.Equal(new DisplayViewport(1912, -30, 1920, 1080), windows.Bounds(host.Hwnd));
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_ExposesTheResolutionUsedForNativeShellParentageVerification()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi();
        windows.SetBounds(100, new DisplayViewport(0, 0, 1920, 1080));
        var resolution = new DesktopHostResolution(100, "WorkerWSibling", false, null);
        var provider = new DesktopHostProvider(dispatcher, new FakeDesktopHostResolver(resolution), windows);

        var host = await provider.CreateAsync(
            Monitor("DISPLAY-A", new DisplayViewport(0, 0, 1920, 1080)),
            TestContext.Current.CancellationToken);

        Assert.Equal(resolution, Assert.IsType<WallpaperHostWindow>(host).DesktopResolution);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task CreateAsync_RaisedDesktop_ConfiguresHostAndRendererAsOpaqueLayeredChildren()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeWallpaperHostWindowApi();
        windows.SetBounds(100, new DisplayViewport(0, 0, 1920, 1080));
        windows.AddChildWindow(3000);
        var resolution = new DesktopHostResolution(
            100,
            "ProgmanRaisedDesktop",
            false,
            null,
            ShellViewHwnd: 200,
            RequiresLayeredChildren: true);
        var provider = new DesktopHostProvider(dispatcher, new FakeDesktopHostResolver(resolution), windows);

        var host = await provider.CreateAsync(
            Monitor("DISPLAY-A", new DisplayViewport(0, 0, 1920, 1080)),
            TestContext.Current.CancellationToken);
        await dispatcher.InvokeAsync(_ =>
        {
            host.SetRendererChild(3000);
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.Equal(new[] { host.Hwnd, (nint)3000 }, windows.OpaqueLayeredWindows);
        Assert.Equal((nint)200, windows.InsertAfter(host.Hwnd));
        await provider.DisposeAsync();
    }

    private static MonitorDescriptor Monitor(string identity, DisplayViewport bounds)
    {
        var evidence = new MonitorIdentityEvidence(0, 0, 0, null, null, null, null, null, null, identity, bounds);
        return new MonitorDescriptor(
            new MonitorIdentity(identity), evidence, identity, bounds, bounds, 96, 1,
            bounds.Width >= bounds.Height ? DisplayOrientation.Landscape : DisplayOrientation.Portrait,
            isPrimary: bounds.X == 0);
    }
}

internal sealed class FakeDesktopHostResolver(DesktopHostResolution resolution) : IDesktopHostResolver
{
    public DesktopHostResolution Resolution { get; set; } = resolution;
    public DesktopHostResolution Resolve() => Resolution;
}

internal sealed class FakeWallpaperHostWindowApi : IWallpaperHostWindowApi
{
    private readonly Dictionary<nint, DisplayViewport> _bounds = [];
    private readonly Dictionary<nint, nint> _parents = [];
    private readonly HashSet<nint> _live = [];
    private readonly HashSet<nint> _visible = [];
    private nint _next = 1000;

    public List<nint> OpaqueLayeredWindows { get; } = [];
    private readonly Dictionary<nint, nint> _insertAfter = [];

    public PointAdjustment ClientOriginAdjustment { get; init; }

    public void SetBounds(nint hwnd, DisplayViewport bounds)
    {
        _bounds[hwnd] = bounds;
        _live.Add(hwnd);
    }

    public DisplayViewport Bounds(nint hwnd) => _bounds[hwnd];
    public bool IsVisible(nint hwnd) => _visible.Contains(hwnd);
    public nint InsertAfter(nint hwnd) => _insertAfter.GetValueOrDefault(hwnd);
    public void AddChildWindow(nint hwnd)
    {
        _live.Add(hwnd);
        _bounds[hwnd] = new DisplayViewport(0, 0, 1, 1);
    }
    public void DestroyExternally(nint hwnd) => _live.Remove(hwnd);
    public bool IsWindow(nint hwnd) => _live.Contains(hwnd);
    public DisplayViewport GetWindowBounds(nint hwnd) => _bounds.GetValueOrDefault(hwnd) ?? new DisplayViewport(0, 0, 3840, 1080);

    public nint CreateHostWindow(nint parentHwnd, DisplayViewport relativeBounds)
    {
        var hwnd = _next++;
        _parents[hwnd] = parentHwnd;
        _bounds[hwnd] = relativeBounds;
        _live.Add(hwnd);
        return hwnd;
    }

    public void MoveWindow(nint hwnd, DisplayViewport relativeBounds) => _bounds[hwnd] = relativeBounds;
    public void SetRendererParent(nint rendererHwnd, nint hostHwnd)
    {
        _parents[rendererHwnd] = hostHwnd;
        var hostBounds = _bounds[hostHwnd];
        _bounds[rendererHwnd] = new DisplayViewport(0, 0, hostBounds.Width, hostBounds.Height);
    }
    public void ConfigureOpaqueLayeredWindow(nint hwnd, nint insertAfter)
    {
        OpaqueLayeredWindows.Add(hwnd);
        _insertAfter[hwnd] = insertAfter;
    }
    public (int X, int Y) ScreenToClient(nint parentHwnd, int screenX, int screenY)
    {
        var parent = _bounds[parentHwnd];
        return (
            screenX - parent.X + ClientOriginAdjustment.X,
            screenY - parent.Y + ClientOriginAdjustment.Y);
    }
    public void SetWindowVisible(nint hwnd, bool visible)
    {
        if (visible) _visible.Add(hwnd);
        else _visible.Remove(hwnd);
    }
    public void DestroyWindow(nint hwnd) => _live.Remove(hwnd);
}

internal readonly record struct PointAdjustment(int X, int Y);
