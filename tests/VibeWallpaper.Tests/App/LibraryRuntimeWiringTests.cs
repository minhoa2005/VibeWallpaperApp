using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.App;

public sealed class LibraryRuntimeWiringTests
{
    [Fact]
    public async Task ImportedWallpaper_IsImmediatelyResolvableAndAssignableFromRuntimeSnapshot()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            new InMemoryStateStore());
        var item = VideoItem("Imported");
        var library = new LibraryController(new StaticPreparer(item), coordinator);

        var imported = await library.ImportVideoAsync(
            item.Definition.Source is VideoSource source ? source.FilePath : throw new InvalidOperationException(),
            TestContext.Current.CancellationToken);
        var definition = Assert.IsType<WallpaperDefinition>(RuntimeWallpaperResolver.Find(
            coordinator.GetSnapshot(),
            imported.ImportedItem!.Definition.Id));

        var result = await coordinator.ApplyAsync(
            new AssignmentRequest(
                definition,
                DisplayMode.Independent,
                null,
                new DisplayViewport(0, 0, 1920, 1080),
                [new OutputAssignmentTarget(
                    new MonitorIdentity("DISPLAY-A"),
                    101,
                    new DisplayViewport(0, 0, 1920, 1080),
                    new OutputWallpaperSettings(definition.Fit, definition.TargetFps, definition.VolumePercent))]),
            TestContext.Current.CancellationToken);

        Assert.True(imported.Result.Succeeded);
        Assert.Equal(AssignmentOutcome.Applied, result.Outcome);
        Assert.Equal(
            imported.ImportedItem.Definition.Id,
            Assert.Single(coordinator.GetSnapshot().State.Assignments).Wallpaper);
    }

    [Fact]
    public void Resolve_MissingRuntimeItem_DoesNotUseAStartupStateFallback()
    {
        var stale = VideoItem("Startup only");
        var runtime = new EngineSnapshot(PersistedState.Default, []);

        Assert.Null(RuntimeWallpaperResolver.Find(runtime, stale.Definition.Id));
    }

    private static WallpaperLibraryItem VideoItem(string name)
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(),
            name,
            VideoSource.Create(Path.GetFullPath($"{name}.mp4")),
            FitMode.Cover,
            30,
            false,
            false,
            0,
            false);
        return new WallpaperLibraryItem(
            definition,
            null,
            new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(10), 30, "h264", false),
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UnixEpoch));
    }

    private sealed class StaticPreparer(WallpaperLibraryItem item) : IWallpaperImportPreparer
    {
        public Task<WallpaperLibraryItem> PrepareVideoAsync(string sourcePath, CancellationToken cancellationToken) =>
            Task.FromResult(item);

        public Task<WallpaperLibraryItem> PrepareWebAsync(string sourceDirectory, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<WallpaperLibraryItem> RevalidateAsync(WallpaperLibraryItem current, CancellationToken cancellationToken) =>
            Task.FromResult(current);
    }
}
