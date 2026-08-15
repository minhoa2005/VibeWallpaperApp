using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Tests.Runtime.Fakes;

namespace VibeWallpaper.Tests.Library;

public sealed class WallpaperLibraryServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vibe-library-{Guid.NewGuid():N}");
    private readonly DateTimeOffset _now = new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    public WallpaperLibraryServiceTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task ImportVideoAsync_StoresAbsoluteReferenceWithoutCopyingOrModifyingSource()
    {
        var source = CreateVideo("thử nghiệm.mp4", [1, 2, 3, 4]);
        var before = File.GetLastWriteTimeUtc(source);
        var store = new InMemoryStateStore();
        var service = CreateService(store, new FakeVideoProbeService());

        var item = await service.ImportVideoAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(Path.GetFullPath(source), Assert.IsType<VideoSource>(item.Definition.Source).FilePath);
        Assert.Equal(SourceValidationStatus.Available, item.Validation.Status);
        Assert.Equal([1, 2, 3, 4], await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(before, File.GetLastWriteTimeUtc(source));
        Assert.Single(store.State.Library);
        Assert.Equal(1, store.SaveCount);
        Assert.DoesNotContain(Directory.EnumerateFiles(_directory), path =>
            !string.Equals(path, source, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RevalidateAsync_WhenAssignedFileDisappears_PreservesAssignment()
    {
        var source = CreateVideo("assigned.mp4", [5, 6, 7]);
        var store = new InMemoryStateStore();
        var service = CreateService(store, new FakeVideoProbeService());
        var item = await service.ImportVideoAsync(source, TestContext.Current.CancellationToken);
        var monitor = new MonitorIdentity("DISPLAY-A");
        store.Replace(AddAssignment(store.State, item.Definition.Id, monitor));
        File.Delete(source);

        var validation = await service.RevalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SourceValidationStatus.Missing, validation.Status);
        Assert.Equal(item.Definition.Id, Assert.Single(store.State.Assignments).Wallpaper);
        Assert.Equal(item.Definition.Id, Assert.Single(store.State.Library).Definition.Id);
    }

    [Theory]
    [InlineData("wallpaper.txt")]
    [InlineData("wallpaper.exe")]
    [InlineData("wallpaper.mp3")]
    public async Task ImportVideoAsync_RejectsUnsupportedExtensionWithoutPersisting(string name)
    {
        var source = CreateVideo(name, [1]);
        var store = new InMemoryStateStore();
        var service = CreateService(store, new FakeVideoProbeService());

        var error = await Assert.ThrowsAsync<WallpaperImportException>(
            () => service.ImportVideoAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal(SourceValidationStatus.Unsupported, error.Status);
        Assert.Empty(store.State.Library);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task ImportVideoAsync_RejectsDirectoryWithoutCallingProbe()
    {
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService();
        var service = CreateService(store, probe);

        var error = await Assert.ThrowsAsync<WallpaperImportException>(
            () => service.ImportVideoAsync(_directory, TestContext.Current.CancellationToken));

        Assert.Equal(SourceValidationStatus.Invalid, error.Status);
        Assert.Equal(0, probe.CallCount);
        Assert.Empty(store.State.Library);
    }

    [Fact]
    public async Task ImportVideoAsync_RejectsCorruptSupportedFileWithoutPersisting()
    {
        var source = CreateVideo("corrupt.mkv", [0xBA, 0xD0]);
        var store = new InMemoryStateStore();
        var service = CreateService(store, new FakeVideoProbeService
        {
            Failure = new VideoProbeException("video.probe.invalid", "Media has no playable video track."),
        });

        var error = await Assert.ThrowsAsync<WallpaperImportException>(
            () => service.ImportVideoAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal(SourceValidationStatus.Invalid, error.Status);
        Assert.Equal("video.probe.invalid", error.DiagnosticCode);
        Assert.Empty(store.State.Library);
    }

    [Fact]
    public async Task ImportVideoAsync_WhenFileDisappearsAfterProbe_RejectsWithoutPersisting()
    {
        var source = CreateVideo("race.mov", [9, 8, 7]);
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService { AfterProbe = () => File.Delete(source) };
        var service = CreateService(store, probe);

        var error = await Assert.ThrowsAsync<WallpaperImportException>(
            () => service.ImportVideoAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal(SourceValidationStatus.Missing, error.Status);
        Assert.Empty(store.State.Library);
        Assert.Equal(0, store.SaveCount);
    }

    [Fact]
    public async Task ImportVideoAsync_WhenFileChangesDuringProbe_RejectsMismatchedMetadata()
    {
        var source = CreateVideo("probe-race.mp4", [1, 2, 3]);
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService
        {
            AfterProbe = () => File.WriteAllBytes(source, [9, 8, 7, 6]),
        };
        var service = CreateService(store, probe);

        var error = await Assert.ThrowsAsync<WallpaperImportException>(
            () => service.ImportVideoAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal(SourceValidationStatus.Changed, error.Status);
        Assert.Equal("video.source.changed_during_import", error.DiagnosticCode);
        Assert.Empty(store.State.Library);
    }

    [Fact]
    public async Task ImportVideoAsync_WhenFileChangesAndProbeThrows_ClassifiesChangedNotInvalid()
    {
        var source = CreateVideo("probe-throw-race.mp4", [1, 2, 3]);
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService
        {
            OnProbe = () => File.WriteAllBytes(source, [9, 8, 7, 6]),
            Failure = new VideoProbeException("video.probe.invalid", "stale parse failed"),
        };
        var service = CreateService(store, probe);

        var error = await Assert.ThrowsAsync<WallpaperImportException>(
            () => service.ImportVideoAsync(source, TestContext.Current.CancellationToken));

        Assert.Equal(SourceValidationStatus.Changed, error.Status);
        Assert.Equal("video.source.changed_during_import", error.DiagnosticCode);
        Assert.Empty(store.State.Library);
    }

    [Fact]
    public async Task RevalidateAsync_WhenCheapMetadataMatches_DoesNotProbeAgain()
    {
        var source = CreateVideo("unchanged.webm", [4, 3, 2, 1]);
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService();
        var service = CreateService(store, probe);
        var item = await service.ImportVideoAsync(source, TestContext.Current.CancellationToken);

        var validation = await service.RevalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SourceValidationStatus.Available, validation.Status);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task RevalidateAsync_WhenFileChanged_ReprobesAndRefreshesStamp()
    {
        var source = CreateVideo("changed.gif", [1, 2]);
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService();
        var service = CreateService(store, probe);
        var item = await service.ImportVideoAsync(source, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(source, "changed", TestContext.Current.CancellationToken);

        var validation = await service.RevalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SourceValidationStatus.Available, validation.Status);
        Assert.Equal(2, probe.CallCount);
        Assert.Equal(new FileInfo(source).Length, validation.Stamp!.Length);
    }

    [Fact]
    public async Task RevalidateAsync_WhenFileChangesDuringProbe_MarksChangedWithoutPersistingNewMetadata()
    {
        var source = CreateVideo("revalidate-race.mp4", [1, 2, 3]);
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService();
        var service = CreateService(store, probe);
        var item = await service.ImportVideoAsync(source, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(source, "trigger", TestContext.Current.CancellationToken);
        probe.AfterProbe = () => File.AppendAllText(source, "during-probe");

        var validation = await service.RevalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SourceValidationStatus.Changed, validation.Status);
        Assert.Equal("video.source.changed_during_validation", validation.DiagnosticCode);
        Assert.Equal(item.Video, Assert.Single(store.State.Library).Video);
    }

    [Fact]
    public async Task RevalidateAsync_WhenFileChangesAndProbeThrows_MarksChangedNotInvalid()
    {
        var source = CreateVideo("revalidate-throw-race.mp4", [1, 2, 3]);
        var store = new InMemoryStateStore();
        var probe = new FakeVideoProbeService();
        var service = CreateService(store, probe);
        var item = await service.ImportVideoAsync(source, TestContext.Current.CancellationToken);
        await File.AppendAllTextAsync(source, "trigger", TestContext.Current.CancellationToken);
        probe.OnProbe = () => File.AppendAllText(source, "during-failed-probe");
        probe.Failure = new VideoProbeException("video.probe.invalid", "stale parse failed");

        var validation = await service.RevalidateAsync(item.Definition.Id, TestContext.Current.CancellationToken);

        Assert.Equal(SourceValidationStatus.Changed, validation.Status);
        Assert.Equal("video.source.changed_during_validation", validation.DiagnosticCode);
        Assert.Equal(item.Video, Assert.Single(store.State.Library).Video);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private WallpaperLibraryService CreateService(InMemoryStateStore store, IVideoProbeService probe) =>
        new(store, probe, thumbnailService: null, TestTimeProvider.Create(_now));

    private string CreateVideo(string name, byte[] content)
    {
        var path = Path.Combine(_directory, name);
        File.WriteAllBytes(path, content);
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime);
        return path;
    }

    private static PersistedState AddAssignment(PersistedState state, WallpaperId wallpaper, MonitorIdentity monitor)
    {
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var evidence = new MonitorIdentityEvidence(1, 1, 1, null, null, null, null, null, null, monitor.Key, bounds);
        var assignment = new WallpaperAssignment(
            new PersistedMonitorReference(monitor, evidence), wallpaper, DisplayMode.Independent,
            FitMode.Cover, 30, 0, null);
        return new PersistedState(state.SchemaVersion, state.Library, [assignment], state.Groups, state.AudioOwner);
    }

    private sealed class FakeVideoProbeService : IVideoProbeService
    {
        public int CallCount { get; private set; }
        public Exception? Failure { get; set; }
        public Action? OnProbe { get; set; }
        public Action? AfterProbe { get; set; }

        public Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            OnProbe?.Invoke();
            if (Failure is not null) throw Failure;
            AfterProbe?.Invoke();
            return Task.FromResult(new VideoMetadata(320, 180, TimeSpan.FromSeconds(1), 30, "test", false));
        }
    }
}

internal static class TestTimeProvider
{
    public static TimeProvider Create(DateTimeOffset value) => new FixedTimeProvider(value);

    private sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }
}
