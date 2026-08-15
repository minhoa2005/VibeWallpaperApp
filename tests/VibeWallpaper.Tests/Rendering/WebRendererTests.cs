using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Web;

namespace VibeWallpaper.Tests.Rendering;

public sealed class WebRendererTests
{
    [Fact]
    public void Factory_PassesSelectedDefinitionToAdapterFactory()
    {
        WallpaperDefinition? received = null;
        var adapter = new FakeAdapter();
        var factory = new WebRendererFactory(definition =>
        {
            received = definition;
            return adapter;
        });
        var definition = new WallpaperDefinition(
            WallpaperId.New(), "Web", WebSource.Create("C:\\wallpaper", "index.html"),
            FitMode.Cover, 30, false, false, 0, false);

        var renderer = factory.Create(definition);

        Assert.IsType<WebRenderer>(renderer);
        Assert.Same(definition, received);
    }

    [Fact]
    public async Task Lifecycle_LoadActivateAndSuspend_KeepsFailedSuspendHidden()
    {
        var adapter = new FakeAdapter { SuspendResult = false };
        await using var renderer = new WebRenderer(adapter);
        var monitor = new MonitorDescriptor(new MonitorIdentity("A"), new MonitorIdentityEvidence(1, 1, 1, null, null, "DISPLAY#A", "ACM", 1, 1, "A", new DisplayViewport(0, 0, 1920, 1080)), "A", new DisplayViewport(0, 0, 1920, 1080), new DisplayViewport(0, 0, 1920, 1080), 96, 1, DisplayOrientation.Landscape, true);
        var context = new RendererContext((nint)1, monitor, new DisplayViewport(0, 0, 1920, 1080), monitor.Bounds);
        var source = WebSource.Create("C:\\wallpaper", "index.html");

        await renderer.InitializeAsync(context, TestContext.Current.CancellationToken);
        await renderer.LoadAsync(source, TestContext.Current.CancellationToken);
        await renderer.ActivateAsync(TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), TestContext.Current.CancellationToken);

        Assert.False(adapter.Visible);
        Assert.Equal(RendererLifecycle.Active, renderer.Lifecycle);
        Assert.Equal([null, null], adapter.ThrottleRequests);
    }

    [Fact]
    public async Task ApplyPerformanceAsync_ActiveThrottle_ForwardsAndUpdatesRequestedFps()
    {
        var adapter = new FakeAdapter();
        await using var renderer = new WebRenderer(adapter);
        var monitor = new MonitorDescriptor(new MonitorIdentity("A"), new MonitorIdentityEvidence(1, 1, 1, null, null, "DISPLAY#A", "ACM", 1, 1, "A", new DisplayViewport(0, 0, 1920, 1080)), "A", new DisplayViewport(0, 0, 1920, 1080), new DisplayViewport(0, 0, 1920, 1080), 96, 1, DisplayOrientation.Landscape, true);
        var context = new RendererContext((nint)1, monitor, new DisplayViewport(0, 0, 1920, 1080), monitor.Bounds);
        var source = WebSource.Create("C:\\wallpaper", "index.html");

        await renderer.InitializeAsync(context, TestContext.Current.CancellationToken);
        await renderer.LoadAsync(source, TestContext.Current.CancellationToken);
        await renderer.ActivateAsync(TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled, 24), TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled, 12), TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), TestContext.Current.CancellationToken);

        Assert.Equal([null, 24, 12, null], adapter.ThrottleRequests);
        Assert.Equal(0, adapter.ResumeCount);
        Assert.True(adapter.Visible);
    }

    [Fact]
    public async Task Activate_WhenAlreadyThrottled_AppliesPendingThrottle()
    {
        var adapter = new FakeAdapter();
        await using var renderer = new WebRenderer(adapter);
        var monitor = new MonitorDescriptor(new MonitorIdentity("A"), new MonitorIdentityEvidence(1, 1, 1, null, null, "DISPLAY#A", "ACM", 1, 1, "A", new DisplayViewport(0, 0, 1920, 1080)), "A", new DisplayViewport(0, 0, 1920, 1080), new DisplayViewport(0, 0, 1920, 1080), 96, 1, DisplayOrientation.Landscape, true);
        var context = new RendererContext((nint)1, monitor, new DisplayViewport(0, 0, 1920, 1080), monitor.Bounds);
        var source = WebSource.Create("C:\\wallpaper", "index.html");

        await renderer.InitializeAsync(context, TestContext.Current.CancellationToken);
        await renderer.LoadAsync(source, TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled, 18), TestContext.Current.CancellationToken);
        await renderer.ActivateAsync(TestContext.Current.CancellationToken);

        Assert.Equal([18], adapter.ThrottleRequests);
        Assert.True(adapter.Visible);
    }

    [Fact]
    public async Task ResumeFromSuspendedThrottle_RestoresThrottleAfterResume()
    {
        var adapter = new FakeAdapter();
        await using var renderer = new WebRenderer(adapter);
        var monitor = new MonitorDescriptor(new MonitorIdentity("A"), new MonitorIdentityEvidence(1, 1, 1, null, null, "DISPLAY#A", "ACM", 1, 1, "A", new DisplayViewport(0, 0, 1920, 1080)), "A", new DisplayViewport(0, 0, 1920, 1080), new DisplayViewport(0, 0, 1920, 1080), 96, 1, DisplayOrientation.Landscape, true);
        var context = new RendererContext((nint)1, monitor, new DisplayViewport(0, 0, 1920, 1080), monitor.Bounds);
        var source = WebSource.Create("C:\\wallpaper", "index.html");

        await renderer.InitializeAsync(context, TestContext.Current.CancellationToken);
        await renderer.LoadAsync(source, TestContext.Current.CancellationToken);
        await renderer.ActivateAsync(TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled, 12), TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), TestContext.Current.CancellationToken);
        await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Throttled, 12), TestContext.Current.CancellationToken);

        Assert.Equal([null, 12, null, 12], adapter.ThrottleRequests);
        Assert.Equal(1, adapter.ResumeCount);
        Assert.True(adapter.Visible);
    }

    private sealed class FakeAdapter : IWebControllerAdapter
    {
        public bool Visible { get; private set; }
        public bool SuspendResult { get; init; } = true;
        public List<int?> ThrottleRequests { get; } = [];
        public int ResumeCount { get; private set; }
        public Task InitializeAsync(RendererContext context, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task NavigateAsync(WebSource source, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetVisibleAsync(bool visible, CancellationToken cancellationToken) { Visible = visible; return Task.CompletedTask; }
        public Task SetPresentationThrottleAsync(int? targetPresentationFps, CancellationToken cancellationToken) { ThrottleRequests.Add(targetPresentationFps); return Task.CompletedTask; }
        public Task<bool> TrySuspendAsync(CancellationToken cancellationToken) => Task.FromResult(SuspendResult);
        public Task ResumeAsync(CancellationToken cancellationToken) { ResumeCount++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
