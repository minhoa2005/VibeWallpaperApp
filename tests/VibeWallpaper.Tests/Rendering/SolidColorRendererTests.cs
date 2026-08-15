using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Solid;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Rendering;

public sealed class SolidColorRendererTests
{
    [Fact]
    public async Task LoadAndActivate_CreatesChildAndPaintsRequestedRgbColor()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeSolidRendererWindowApi();
        var renderer = new SolidColorRenderer(dispatcher, windows);

        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(500), token);
            await renderer.LoadAsync(SolidColorSource.Create("#123456"), token);
            await renderer.ActivateAsync(token);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(RendererLifecycle.Active, renderer.Lifecycle);
        Assert.Equal((nint)500, windows.Parent(renderer.Hwnd));
        Assert.Equal(0x00563412u, windows.Color(renderer.Hwnd));
        Assert.True(windows.IsVisible(renderer.Hwnd));
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task PerformanceTransitions_RepeatedThrottledAndSuspendedAreIdempotent()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeSolidRendererWindowApi();
        var renderer = new SolidColorRenderer(dispatcher, windows);

        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(500), token);
            await renderer.LoadAsync(SolidColorSource.Create("#000000"), token);
            await renderer.ActivateAsync(token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled), token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled), token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token);
            await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(PerformanceState.Suspended, renderer.PerformanceState);
        Assert.False(windows.IsVisible(renderer.Hwnd));
        Assert.Equal(2, windows.VisibilityChangeCount);
        await renderer.DisposeAsync();
    }

    [Fact]
    public async Task Dispose_DestroysOwnedChildButLeavesBorrowedHostAlive()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeSolidRendererWindowApi();
        windows.AddBorrowedHost(500);
        var renderer = new SolidColorRenderer(dispatcher, windows);
        await dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(500), token);
            await renderer.LoadAsync(SolidColorSource.Create("#FFFFFF"), token);
        }, TestContext.Current.CancellationToken);
        var child = renderer.Hwnd;

        await renderer.DisposeAsync();

        Assert.False(windows.IsWindow(child));
        Assert.True(windows.IsWindow(500));
        Assert.Equal(RendererLifecycle.Disposed, renderer.Lifecycle);
    }

    private static RendererContext Context(nint host)
    {
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var identity = new MonitorIdentity("DISPLAY-A");
        var evidence = new MonitorIdentityEvidence(0, 0, 0, null, null, null, null, null, null, identity.Key, bounds);
        var monitor = new MonitorDescriptor(identity, evidence, identity.Key, bounds, bounds, 96, 1, DisplayOrientation.Landscape, true);
        return new RendererContext(host, monitor, bounds, bounds);
    }
}

internal sealed class FakeSolidRendererWindowApi : ISolidRendererWindowApi
{
    private readonly Dictionary<nint, nint> _parents = [];
    private readonly Dictionary<nint, uint> _colors = [];
    private readonly Dictionary<nint, bool> _visibility = [];
    private readonly HashSet<nint> _live = [];
    private nint _next = 2000;

    public int VisibilityChangeCount { get; private set; }
    public void AddBorrowedHost(nint hwnd) => _live.Add(hwnd);
    public nint Parent(nint hwnd) => _parents[hwnd];
    public uint Color(nint hwnd) => _colors[hwnd];
    public bool IsVisible(nint hwnd) => _visibility.GetValueOrDefault(hwnd);
    public bool IsWindow(nint hwnd) => _live.Contains(hwnd);

    public nint CreateRendererWindow(nint parentHwnd)
    {
        var hwnd = _next++;
        _parents[hwnd] = parentHwnd;
        _visibility[hwnd] = false;
        _live.Add(hwnd);
        return hwnd;
    }

    public void SetColor(nint hwnd, uint color) => _colors[hwnd] = color;
    public void Invalidate(nint hwnd) { }

    public void SetVisible(nint hwnd, bool visible)
    {
        if (_visibility[hwnd] != visible)
        {
            VisibilityChangeCount++;
            _visibility[hwnd] = visible;
        }
    }

    public void DestroyWindow(nint hwnd) => _live.Remove(hwnd);
}
