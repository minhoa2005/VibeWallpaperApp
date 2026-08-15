using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Runtime;

public sealed class LibraryStateAuthorityTests
{
    private static readonly MonitorIdentity Output = new("DISPLAY-A");

    [Fact]
    public async Task AddThenAssign_PersistsImportedItemAndPublishesItOnce()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var store = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            store);
        var authority = (ILibraryStateAuthority)coordinator;
        var item = Item("Aurora");

        await authority.AddLibraryItemAsync(item, TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(
            Request(item.Definition),
            TestContext.Current.CancellationToken);

        var state = coordinator.GetSnapshot().State;
        Assert.Equal(item.Definition.Id, Assert.Single(state.Library).Definition.Id);
        Assert.Equal(item.Definition.Id, Assert.Single(state.Assignments).Wallpaper);
        Assert.Equal(1, authority.GetLibrarySnapshot().Version);
    }

    [Fact]
    public async Task Add_WhenSaveFails_DoesNotPublishOrChangeRuntimeState()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var store = new InMemoryStateStore
        {
            NextSaveFailure = new IOException("injected"),
        };
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            store);
        var authority = (ILibraryStateAuthority)coordinator;

        await Assert.ThrowsAsync<IOException>(() =>
            authority.AddLibraryItemAsync(Item("Aurora"), TestContext.Current.CancellationToken));

        Assert.Empty(coordinator.GetSnapshot().State.Library);
        Assert.Empty(store.State.Library);
        Assert.Equal(0, authority.GetLibrarySnapshot().Version);
    }

    [Fact]
    public async Task Add_DuplicateIdRejectsWithoutSecondSave()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var store = new InMemoryStateStore();
        var authority = (ILibraryStateAuthority)new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            store);
        var item = Item("Aurora");
        await authority.AddLibraryItemAsync(item, TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<LibraryStateException>(() =>
            authority.AddLibraryItemAsync(item, TestContext.Current.CancellationToken));

        Assert.Equal("library.item.duplicate", error.Code);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task Replace_MissingIdReturnsTypedErrorWithoutChangingVersion()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var authority = (ILibraryStateAuthority)new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            new InMemoryStateStore());

        var error = await Assert.ThrowsAsync<LibraryStateException>(() =>
            authority.ReplaceLibraryItemAsync(Item("Missing"), TestContext.Current.CancellationToken));

        Assert.Equal("library.item.missing", error.Code);
        Assert.Equal(0, authority.GetLibrarySnapshot().Version);
    }

    [Fact]
    public async Task SetWebNetworkPermission_PreservesIdentitySettingsAndAssignment()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var store = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            store);
        var authority = (ILibraryStateAuthority)coordinator;
        var item = WebItem("Local");
        await authority.AddLibraryItemAsync(item, TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request(item.Definition), TestContext.Current.CancellationToken);

        var next = await authority.SetWebNetworkPermissionAsync(
            item.Definition.Id,
            true,
            TestContext.Current.CancellationToken);

        var updated = Assert.Single(next.Items);
        Assert.Equal(item.Definition.Id, updated.Definition.Id);
        Assert.Equal(item.Definition.Name, updated.Definition.Name);
        Assert.True(updated.Definition.NetworkEnabled);
        Assert.Equal(item.Definition.Id, Assert.Single(coordinator.GetSnapshot().State.Assignments).Wallpaper);
    }

    [Fact]
    public async Task RemoveAssignedItem_WhenClearAssignmentsIsFalse_RejectsWithoutChange()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var store = new InMemoryStateStore();
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            store);
        var authority = (ILibraryStateAuthority)coordinator;
        var item = Item("Assigned");
        await authority.AddLibraryItemAsync(item, TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request(item.Definition), TestContext.Current.CancellationToken);

        var error = await Assert.ThrowsAsync<LibraryStateException>(() =>
            authority.RemoveLibraryItemAsync(item.Definition.Id, false, TestContext.Current.CancellationToken));

        Assert.Equal("library.item.assigned", error.Code);
        Assert.Single(coordinator.GetSnapshot().State.Library);
        Assert.Single(coordinator.GetSnapshot().State.Assignments);
    }

    [Fact]
    public async Task RemoveAssignedItem_WhenConfirmed_ClearsAssignmentAndDisposesRenderer()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var store = new InMemoryStateStore();
        var factory = new FakeWallpaperRendererFactory();
        var coordinator = new WallpaperAssignmentCoordinator(dispatcher, factory, store);
        var authority = (ILibraryStateAuthority)coordinator;
        var item = Item("Assigned");
        await authority.AddLibraryItemAsync(item, TestContext.Current.CancellationToken);
        await coordinator.ApplyAsync(Request(item.Definition), TestContext.Current.CancellationToken);

        await authority.RemoveLibraryItemAsync(
            item.Definition.Id,
            true,
            TestContext.Current.CancellationToken);

        Assert.Empty(coordinator.GetSnapshot().State.Library);
        Assert.Empty(coordinator.GetSnapshot().State.Assignments);
        Assert.Equal(1, factory.Renderer("Assigned").DisposeCount);
    }

    private static WallpaperLibraryItem Item(string name)
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(),
            name,
            VideoSource.Create(Path.GetFullPath($"{name}.mp4")),
            FitMode.Cover,
            30,
            false,
            true,
            50,
            false);
        return new WallpaperLibraryItem(
            definition,
            null,
            new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(10), 30, "h264", true),
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UnixEpoch));
    }

    private static WallpaperLibraryItem WebItem(string name)
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(),
            name,
            WebSource.Create(Path.GetFullPath(name), "index.html"),
            FitMode.Contain,
            24,
            false,
            false,
            0,
            false);
        return new WallpaperLibraryItem(
            definition,
            null,
            null,
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UnixEpoch));
    }

    private static AssignmentRequest Request(WallpaperDefinition definition) => new(
        definition,
        DisplayMode.Independent,
        null,
        new DisplayViewport(0, 0, 1920, 1080),
        [new OutputAssignmentTarget(
            Output,
            101,
            new DisplayViewport(0, 0, 1920, 1080),
            new OutputWallpaperSettings(definition.Fit, definition.TargetFps, definition.VolumePercent))]);
}
