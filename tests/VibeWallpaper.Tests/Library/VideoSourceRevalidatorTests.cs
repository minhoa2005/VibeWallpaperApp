using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Engine.Sources;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Library;

public sealed class VideoSourceRevalidatorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"source-revalidator-{Guid.NewGuid():N}.mp4");
    private readonly MonitorIdentity _output = new("DISPLAY-A");

    [Fact]
    public async Task RevalidateBeforeActivationAsync_MissingThenReturned_UsesFallbackThenRestoresWithNewGeneration()
    {
        File.WriteAllBytes(_path, [1, 2, 3]);
        var store = new InMemoryStateStore();
        var library = new WallpaperLibraryService(store, new PlayableProbe());
        var item = await library.ImportVideoAsync(_path, TestContext.Current.CancellationToken);
        store.Replace(WithAssignment(store.State, item.Definition.Id));
        var activator = new RecordingActivator();
        var fallback = new FallbackRendererCoordinator(store.State, AppSettings.Default, activator);
        await fallback.InitializeAsync(_ => true, TestContext.Current.CancellationToken);
        var revalidator = new VideoSourceRevalidator(store, library, fallback, _ => true);
        File.Delete(_path);

        var missing = await revalidator.RevalidateBeforeActivationAsync(
            item.Definition.Id, TestContext.Current.CancellationToken);
        File.WriteAllBytes(_path, [1, 2, 3]);
        var returned = await revalidator.RevalidateBeforeActivationAsync(
            item.Definition.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SourceValidationStatus.Missing, missing.Status);
        Assert.Equal(SourceValidationStatus.Available, returned.Status);
        Assert.Equal([1L, 2L, 3L], activator.Generations);
        Assert.Equal(EffectiveWallpaperKind.Assigned, fallback.GetEffectiveState(_output).EffectiveKind);
        Assert.Equal(item.Definition.Id, Assert.Single(store.State.Assignments).Wallpaper);
    }

    [Fact]
    public async Task ActiveMonitor_InitialAndPeriodicChecksReconcileOnlyAssignedVideoSources()
    {
        File.WriteAllBytes(_path, [1, 2, 3]);
        var store = new InMemoryStateStore();
        var library = new WallpaperLibraryService(store, new PlayableProbe());
        var item = await library.ImportVideoAsync(_path, TestContext.Current.CancellationToken);
        store.Replace(WithAssignment(store.State, item.Definition.Id));
        var activator = new RecordingActivator();
        var fallback = new FallbackRendererCoordinator(store.State, AppSettings.Default, activator);
        await fallback.InitializeAsync(_ => true, TestContext.Current.CancellationToken);
        var revalidator = new VideoSourceRevalidator(store, library, fallback, _ => true);
        await using var changes = new SourceChangeMonitor(store);
        await using var active = new ActiveVideoSourceMonitor(
            store, changes, revalidator, TimeSpan.FromMinutes(1), TimeSpan.FromSeconds(1));
        File.Delete(_path);

        await active.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(EffectiveWallpaperKind.SolidFallback, fallback.GetEffectiveState(_output).EffectiveKind);
        Assert.Equal(SourceValidationStatus.Missing, Assert.Single(store.State.Library).Validation.Status);
    }

    public void Dispose()
    {
        if (File.Exists(_path)) File.Delete(_path);
    }

    private PersistedState WithAssignment(PersistedState state, WallpaperId wallpaper)
    {
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var evidence = new MonitorIdentityEvidence(1, 1, 1, null, null, null, null, null, null, _output.Key, bounds);
        var assignment = new WallpaperAssignment(
            new PersistedMonitorReference(_output, evidence), wallpaper, DisplayMode.Independent,
            FitMode.Cover, 30, 0, null);
        return new PersistedState(state.SchemaVersion, state.Library, [assignment], state.Groups, state.AudioOwner);
    }

    private sealed class PlayableProbe : IVideoProbeService
    {
        public Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new VideoMetadata(320, 180, TimeSpan.FromSeconds(1), 30, "test", false));
        }
    }

    private sealed class RecordingActivator : IRuntimeWallpaperActivator
    {
        public List<long> Generations { get; } = [];

        public Task ActivateAsync(
            MonitorIdentity output,
            WallpaperDefinition wallpaper,
            WallpaperAssignment persistedAssignment,
            long generation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Generations.Add(generation);
            return Task.CompletedTask;
        }
    }
}
