using System.Security.Cryptography;
using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.App;

public sealed class SourceIntegrityWorkflowTests
{
    [Fact]
    public async Task SourcesRemainByteAndMetadataIdenticalAcrossLibraryWorkflow()
    {
        var root = FindTestAssetRoot();
        var videoPath = Directory.GetFiles(root, "*.mp4", SearchOption.AllDirectories).Single();
        var webRoot = Directory.GetDirectories(root, "Web cảnh biển", SearchOption.TopDirectoryOnly).Single();
        var sourceFiles = Directory.GetFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var before = sourceFiles.ToDictionary(static path => path, Evidence, StringComparer.OrdinalIgnoreCase);

        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var coordinator = new WallpaperAssignmentCoordinator(
            dispatcher,
            new FakeWallpaperRendererFactory(),
            new InMemoryStateStore());
        var controller = new LibraryController(
            new WallpaperImportPreparer(new FixedProbe()),
            coordinator);
        var cancellationToken = TestContext.Current.CancellationToken;

        var video = await controller.ImportVideoAsync(videoPath, cancellationToken);
        var web = await controller.ImportWebAsync(webRoot, cancellationToken);
        Assert.True(video.Result.Succeeded);
        Assert.True(web.Result.Succeeded);

        Assert.True((await controller.RevalidateAsync(
            video.ImportedItem!.Definition.Id, cancellationToken)).Succeeded);
        Assert.True((await controller.RevalidateAsync(
            web.ImportedItem!.Definition.Id, cancellationToken)).Succeeded);
        Assert.True((await controller.SetNetworkPermissionAsync(
            web.ImportedItem.Definition.Id, true, cancellationToken)).Succeeded);
        Assert.True((await controller.SetNetworkPermissionAsync(
            web.ImportedItem.Definition.Id, false, cancellationToken)).Succeeded);

        var runtimeVideo = RuntimeWallpaperResolver.Find(
            coordinator.GetSnapshot(), video.ImportedItem.Definition.Id)!;
        var apply = await coordinator.ApplyAsync(
            Request(runtimeVideo),
            cancellationToken);
        Assert.Equal(AssignmentOutcome.Applied, apply.Outcome);

        Assert.True((await controller.RemoveAsync(
            video.ImportedItem.Definition.Id, true, cancellationToken)).Succeeded);
        Assert.True((await controller.RemoveAsync(
            web.ImportedItem.Definition.Id, false, cancellationToken)).Succeeded);

        var after = sourceFiles.ToDictionary(static path => path, Evidence, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(before, after);
    }

    private static FileEvidence Evidence(string path)
    {
        var info = new FileInfo(path);
        using var stream = File.OpenRead(path);
        return new FileEvidence(
            Convert.ToHexString(SHA256.HashData(stream)),
            info.Length,
            info.LastWriteTimeUtc.Ticks);
    }

    private static string FindTestAssetRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "tests", "TestAssets", "LibraryIntegrity");
            if (Directory.Exists(candidate)) return candidate;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate tests\\TestAssets\\LibraryIntegrity above '{AppContext.BaseDirectory}'.");
    }

    private static AssignmentRequest Request(WallpaperDefinition definition) => new(
        definition,
        DisplayMode.Independent,
        null,
        new DisplayViewport(0, 0, 1920, 1080),
        [new OutputAssignmentTarget(
            new MonitorIdentity("DISPLAY-A"),
            101,
            new DisplayViewport(0, 0, 1920, 1080),
            new OutputWallpaperSettings(
                definition.Fit,
                definition.TargetFps,
                definition.VolumePercent))]);

    private sealed record FileEvidence(string Sha256, long Length, long LastWriteUtcTicks);

    private sealed class FixedProbe : IVideoProbeService
    {
        public Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken) =>
            Task.FromResult(new VideoMetadata(
                1920,
                1080,
                TimeSpan.FromSeconds(10),
                30,
                "fixture",
                false));
    }
}
