using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Sources;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Rendering;

public sealed class VideoProbeIntegrationTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vibe-libvlc-{Guid.NewGuid():N}");

    public VideoProbeIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    [Trait("Category", "LibVLCIntegration")]
    public async Task ProbeAsync_CorruptSupportedFile_ReturnsTypedInvalidDiagnosticAndReleasesFile()
    {
        var source = Path.Combine(_directory, "corrupt.mp4");
        await File.WriteAllBytesAsync(source, [0xDE, 0xAD, 0xBE, 0xEF], TestContext.Current.CancellationToken);
        var probe = CreateProbeOrSkip();

        var error = await Assert.ThrowsAsync<VideoProbeException>(
            () => probe.ProbeAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal("video.probe.invalid", error.DiagnosticCode);
        File.Delete(source);
        Assert.False(File.Exists(source));
    }

    [Fact]
    [Trait("Category", "LibVLCIntegration")]
    public async Task ProbeAsync_ValidFixture_ReturnsPositiveDimensionsAndDuration()
    {
        var source = TinyGifTestAsset.Create(_directory, "one-second-pattern.gif");
        var probe = CreateProbeOrSkip();

        var metadata = await probe.ProbeAsync(source, TestContext.Current.CancellationToken);

        Assert.True(metadata.Width > 0);
        Assert.True(metadata.Height > 0);
        Assert.True(metadata.Duration > TimeSpan.Zero);
    }

    [Fact]
    [Trait("Category", "LibVLCIntegration")]
    public async Task GenerateAsync_ValidFixture_WritesRealThumbnailOutsideSourceDirectory()
    {
        var sourceDirectory = Path.Combine(_directory, "source-real");
        var localAppData = Path.Combine(_directory, "local-real");
        var source = TinyGifTestAsset.Create(sourceDirectory, "one-second-pattern.gif");
        var probe = CreateProbeOrSkip();
        var metadata = await probe.ProbeAsync(source, TestContext.Current.CancellationToken);
        var cacheRoot = Path.Combine(localAppData, "VibeWallpaper", "cache", "thumbnails");
        var service = new VideoThumbnailService(new LibVlcVideoSnapshotter(cacheRoot: cacheRoot), localAppData);

        var thumbnail = await service.GenerateAsync(
            WallpaperId.New(), source, metadata, TestContext.Current.CancellationToken);

        Assert.NotNull(thumbnail);
        Assert.True(File.Exists(thumbnail));
        Assert.DoesNotContain(sourceDirectory, thumbnail, StringComparison.OrdinalIgnoreCase);
        Assert.InRange(new FileInfo(thumbnail).Length, 1, 1024 * 1024);
    }

    [Fact]
    [Trait("Category", "LibVLCIntegration")]
    public async Task ImportRevalidateDispose_FiftyCycles_ReleasesSourcesAndDoesNotGrowHandlesContinuously()
    {
        var probe = CreateProbeOrSkip();
        var process = System.Diagnostics.Process.GetCurrentProcess();
        var midpointHandles = 0;

        for (var cycle = 0; cycle < 50; cycle++)
        {
            var source = TinyGifTestAsset.Create(_directory, $"cycle-{cycle}.gif");
            var store = new InMemoryStateStore();
            var library = new WallpaperLibraryService(store, probe);
            var item = await library.ImportVideoAsync(source, TestContext.Current.CancellationToken);
            await using (var monitor = new SourceChangeMonitor(store))
            {
                await monitor.InvalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken);
                var validation = await library.RevalidateAsync(
                    item.Definition.Id, TestContext.Current.CancellationToken);
                Assert.Equal(SourceValidationStatus.Available, validation.Status);
            }

            File.Delete(source);
            Assert.False(File.Exists(source));

            if (cycle == 24)
            {
                CollectReleasedResources();
                process.Refresh();
                midpointHandles = process.HandleCount;
            }
        }

        CollectReleasedResources();
        process.Refresh();
        Assert.InRange(process.HandleCount, 0, midpointHandles + 16);
    }

    [Fact]
    public async Task GenerateAsync_WritesBoundedThumbnailToLocalCacheOutsideSourceDirectory()
    {
        var sourceDirectory = Path.Combine(_directory, "source");
        var localAppData = Path.Combine(_directory, "local");
        Directory.CreateDirectory(sourceDirectory);
        var source = Path.Combine(sourceDirectory, "wallpaper.mp4");
        await File.WriteAllBytesAsync(source, [1], TestContext.Current.CancellationToken);
        var snapshotter = new RecordingSnapshotter();
        var service = new VideoThumbnailService(snapshotter, localAppData);
        var id = WallpaperId.New();

        var thumbnail = await service.GenerateAsync(
            id,
            source,
            new VideoMetadata(3840, 2160, TimeSpan.FromSeconds(10), 30, "test", false),
            TestContext.Current.CancellationToken);

        Assert.NotNull(thumbnail);
        Assert.StartsWith(Path.Combine(localAppData, "VibeWallpaper", "cache", "thumbnails"), thumbnail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(sourceDirectory, thumbnail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((512u, 288u), (snapshotter.Width, snapshotter.Height));
        Assert.True(File.Exists(thumbnail));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private static void CollectReleasedResources()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static VideoProbeService CreateProbeOrSkip()
    {
        try
        {
            return new VideoProbeService();
        }
        catch (LibVlcRuntimeUnavailableException exception)
        {
            Assert.Skip($"Pinned LibVLC x64 runtime unavailable: {exception.Message}");
            throw;
        }
    }

    private sealed class RecordingSnapshotter : IVideoSnapshotter
    {
        public uint Width { get; private set; }
        public uint Height { get; private set; }

        public async Task CaptureAsync(
            string absoluteSourcePath,
            string absoluteDestinationPath,
            uint width,
            uint height,
            CancellationToken cancellationToken)
        {
            Width = width;
            Height = height;
            await File.WriteAllBytesAsync(absoluteDestinationPath, [0x89, 0x50, 0x4E, 0x47], cancellationToken);
        }
    }
}
