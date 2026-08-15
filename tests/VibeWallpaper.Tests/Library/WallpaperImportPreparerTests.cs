using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Import.Video;

namespace VibeWallpaper.Tests.Library;

public sealed class WallpaperImportPreparerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        $"vibe-import-preparer-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 8, 11, 8, 0, 0, TimeSpan.Zero);

    public WallpaperImportPreparerTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task PrepareVideo_ProducesAvailableItemWithoutChangingSource()
    {
        var path = Path.Combine(_directory, "bầu trời.mp4");
        await File.WriteAllBytesAsync(path, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime);
        var beforeBytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        var beforeWrite = File.GetLastWriteTimeUtc(path);
        var preparer = new WallpaperImportPreparer(
            new PlayableProbe(),
            thumbnailService: null,
            TestTimeProvider.Create(_now));

        var item = await preparer.PrepareVideoAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal("bầu trời", item.Definition.Name);
        Assert.Equal(Path.GetFullPath(path), Assert.IsType<VideoSource>(item.Definition.Source).FilePath);
        Assert.Equal(SourceValidationStatus.Available, item.Validation.Status);
        Assert.Equal(beforeBytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(beforeWrite, File.GetLastWriteTimeUtc(path));
    }

    [Fact]
    public async Task PrepareWeb_ValidRootCreatesNetworkOffAvailableItem()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "Bầu trời web")).FullName;
        var entry = Path.Combine(root, "index.html");
        await File.WriteAllTextAsync(entry, "<html>ok</html>", TestContext.Current.CancellationToken);
        File.SetLastWriteTimeUtc(entry, _now.UtcDateTime);
        var preparer = new WallpaperImportPreparer(
            new PlayableProbe(),
            thumbnailService: null,
            TestTimeProvider.Create(_now));

        var item = await preparer.PrepareWebAsync(root, TestContext.Current.CancellationToken);

        var source = Assert.IsType<WebSource>(item.Definition.Source);
        Assert.Equal(Path.GetFullPath(root), source.DirectoryPath);
        Assert.Equal("index.html", source.EntryPoint);
        Assert.False(item.Definition.NetworkEnabled);
        Assert.False(item.Definition.InteractionEnabled);
        Assert.Equal(SourceValidationStatus.Available, item.Validation.Status);
        Assert.NotNull(item.Validation.Stamp?.Fingerprint);
    }

    [Fact]
    public async Task PrepareWeb_MissingIndexReturnsTypedError()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "missing-entry")).FullName;
        var preparer = new WallpaperImportPreparer(
            new PlayableProbe(),
            thumbnailService: null,
            TestTimeProvider.Create(_now));

        var error = await Assert.ThrowsAsync<WallpaperImportException>(() =>
            preparer.PrepareWebAsync(root, TestContext.Current.CancellationToken));

        Assert.Equal(SourceValidationStatus.Invalid, error.Status);
        Assert.Equal("web.entry.missing", error.DiagnosticCode);
    }

    [Fact]
    public async Task RevalidateWeb_WhenFingerprintChanges_RevokesNetworkPermissionAndPreservesId()
    {
        var root = Directory.CreateDirectory(Path.Combine(_directory, "changed-web")).FullName;
        var entry = Path.Combine(root, "index.html");
        await File.WriteAllTextAsync(entry, "one", TestContext.Current.CancellationToken);
        var preparer = new WallpaperImportPreparer(
            new PlayableProbe(),
            thumbnailService: null,
            TestTimeProvider.Create(_now));
        var original = await preparer.PrepareWebAsync(root, TestContext.Current.CancellationToken);
        var enabledDefinition = new WallpaperDefinition(
            original.Definition.Id,
            original.Definition.Name,
            original.Definition.Source,
            original.Definition.Fit,
            original.Definition.TargetFps,
            true,
            false,
            0,
            original.Definition.InteractionEnabled);
        original = new WallpaperLibraryItem(
            enabledDefinition,
            original.ThumbnailCachePath,
            original.Video,
            original.Validation);
        await File.WriteAllTextAsync(entry, "two", TestContext.Current.CancellationToken);

        var updated = await preparer.RevalidateAsync(original, TestContext.Current.CancellationToken);

        Assert.Equal(original.Definition.Id, updated.Definition.Id);
        Assert.False(updated.Definition.NetworkEnabled);
        Assert.Equal(SourceValidationStatus.Available, updated.Validation.Status);
        Assert.NotEqual(original.Validation.Stamp?.Fingerprint, updated.Validation.Stamp?.Fingerprint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private sealed class PlayableProbe : IVideoProbeService
    {
        public Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VideoMetadata(
                1920,
                1080,
                TimeSpan.FromSeconds(10),
                30,
                "h264",
                false));
        }
    }
}
