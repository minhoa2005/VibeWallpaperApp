using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Sources;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Library;

public sealed class SourceChangeMonitorTests
{
    [Fact]
    public async Task RefreshAsync_RegistersVideoImportedAfterMonitoringStarted()
    {
        var store = new InMemoryStateStore();
        await using var monitor = new SourceChangeMonitor(store);
        await monitor.StartAsync(TestContext.Current.CancellationToken);
        var item = Item();
        store.Replace(new PersistedState(1, [item], [], [], null));
        store.SaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await monitor.RefreshAsync(TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(
            Assert.IsType<VideoSource>(item.Definition.Source).FilePath,
            "changed",
            TestContext.Current.CancellationToken);
        await store.SaveStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(SourceValidationStatus.Changed, Assert.Single(store.State.Library).Validation.Status);
    }

    [Fact]
    public async Task InvalidateAsync_EventStormForSameItem_CoalescesToOneChangedCommit()
    {
        var item = Item();
        var store = new InMemoryStateStore(new PersistedState(1, [item], [], [], null));
        await using var monitor = new SourceChangeMonitor(store);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            monitor.InvalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken)));

        Assert.Equal(SourceValidationStatus.Changed, Assert.Single(store.State.Library).Validation.Status);
        Assert.Equal(1, store.SaveCount);
    }

    [Fact]
    public async Task InvalidateAsync_MarksChangedWithoutReadingOrHashingSource()
    {
        var item = Item();
        var store = new InMemoryStateStore(new PersistedState(1, [item], [], [], null));
        await using var monitor = new SourceChangeMonitor(store);
        File.Delete(Assert.IsType<VideoSource>(item.Definition.Source).FilePath);

        await monitor.InvalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken);

        var changed = Assert.Single(store.State.Library);
        Assert.Equal(SourceValidationStatus.Changed, changed.Validation.Status);
        Assert.Equal(item.Validation.Stamp, changed.Validation.Stamp);
    }

    private static WallpaperLibraryItem Item()
    {
        var path = Path.GetFullPath(Path.Combine(Path.GetTempPath(), $"source-monitor-{Guid.NewGuid():N}.mp4"));
        File.WriteAllBytes(path, [1]);
        var definition = new WallpaperDefinition(
            WallpaperId.New(), "video", VideoSource.Create(path), FitMode.Cover, 30, false, false, 0, false);
        return new WallpaperLibraryItem(
            definition, null,
            new VideoMetadata(100, 100, TimeSpan.FromSeconds(1), 30, "test", false),
            new SourceValidation(
                SourceValidationStatus.Available,
                new SourceStamp(1, DateTimeOffset.UnixEpoch, "ABC"),
                null,
                DateTimeOffset.UnixEpoch));
    }
}
