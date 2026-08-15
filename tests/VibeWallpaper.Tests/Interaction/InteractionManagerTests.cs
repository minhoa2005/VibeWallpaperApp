using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Interaction;

namespace VibeWallpaper.Tests.Interaction;

public sealed class InteractionManagerTests
{
    [Fact]
    public async Task EnterAndExit_AreIdempotentAndDestroyEveryOverlay()
    {
        var overlays = new FakeOverlayFactory();
        var manager = new InteractionManager(overlays, [new MonitorIdentity("A"), new MonitorIdentity("B")]);

        Assert.True(await manager.EnterAsync(CancellationToken.None));
        Assert.False(await manager.EnterAsync(CancellationToken.None));
        await manager.ExitAsync(InteractionExitReason.Escape, CancellationToken.None);
        await manager.ExitAsync(InteractionExitReason.ApplicationExit, CancellationToken.None);

        Assert.Equal(2, overlays.Created.Count);
        Assert.All(overlays.Created, overlay => Assert.True(overlay.Destroyed));
        Assert.False(manager.IsActive);
    }

    [Fact]
    public async Task Enter_WhenDesktopContextIsUnavailable_IsRejected()
    {
        var overlays = new FakeOverlayFactory();
        var manager = new InteractionManager(overlays, [], desktopContextAvailable: false);

        Assert.False(await manager.EnterAsync(CancellationToken.None));
        Assert.Empty(overlays.Created);
    }

    private sealed class FakeOverlayFactory : IInteractionOverlayFactory
    {
        public List<FakeOverlay> Created { get; } = [];
        public IInteractionOverlay Create(MonitorIdentity output)
        { var overlay = new FakeOverlay(); Created.Add(overlay); return overlay; }
    }

    private sealed class FakeOverlay : IInteractionOverlay
    {
        public bool Destroyed { get; private set; }
        public void Destroy() => Destroyed = true;
    }
}
