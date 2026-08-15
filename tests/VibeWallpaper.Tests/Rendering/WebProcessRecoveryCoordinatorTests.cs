using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Web;

namespace VibeWallpaper.Tests.Rendering;

public sealed class WebProcessRecoveryCoordinatorTests
{
    [Fact]
    public async Task ConcurrentSharedFailures_InvalidateOnceAndRecreateAllOnce()
    {
        var environment = new FakeEnvironmentProvider();
        var registry = new FakeRegistry();
        var coordinator = new WebProcessRecoveryCoordinator(environment, registry);

        await Task.WhenAll(
            coordinator.HandleAsync(new WebProcessFailure(WebFailureScope.SharedBrowserProcess, null, null, "browser"), CancellationToken.None),
            coordinator.HandleAsync(new WebProcessFailure(WebFailureScope.SharedBrowserProcess, null, null, "browser"), CancellationToken.None));

        Assert.Equal(1, environment.InvalidateCount);
        Assert.Equal(1, registry.RecreateAllCount);
    }

    [Fact]
    public async Task RendererFailure_RecreatesOnlyTheAffectedInstance()
    {
        var environment = new FakeEnvironmentProvider();
        var registry = new FakeRegistry();
        var coordinator = new WebProcessRecoveryCoordinator(environment, registry);
        var id = RendererInstanceId.New();

        await coordinator.HandleAsync(new WebProcessFailure(WebFailureScope.AffectedRenderer, id, null, "renderer"), CancellationToken.None);

        Assert.Equal([id], registry.Recreated);
        Assert.Equal(0, registry.RecreateAllCount);
    }

    private sealed class FakeEnvironmentProvider : IWebEnvironmentProvider
    {
        public long Generation { get; private set; } = 3;
        public int InvalidateCount { get; private set; }
        public Task<WebEnvironmentHandle> GetAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new WebEnvironmentHandle(Generation, "C:\\web"));
        public Task InvalidateAsync(long expectedGeneration, CancellationToken cancellationToken)
        {
            if (expectedGeneration == Generation) { InvalidateCount++; Generation++; }
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeRegistry : IWebRendererRegistry
    {
        public List<RendererInstanceId> Recreated { get; } = [];
        public int RecreateAllCount { get; private set; }
        public IReadOnlyList<WebRendererRegistration> SnapshotActive() => [];
        public Task RecreateAsync(RendererInstanceId rendererInstance, CancellationToken cancellationToken)
        { Recreated.Add(rendererInstance); return Task.CompletedTask; }
        public Task RecreateAllAsync(long environmentGeneration, CancellationToken cancellationToken)
        { RecreateAllCount++; return Task.CompletedTask; }
    }
}
