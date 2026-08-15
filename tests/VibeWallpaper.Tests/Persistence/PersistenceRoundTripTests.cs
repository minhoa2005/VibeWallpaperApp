using System.Text.Json;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Persistence;

namespace VibeWallpaper.Tests.Persistence;

public sealed class PersistenceRoundTripTests
{
    [Fact]
    public void AppSettings_DefaultsMatchSchemaVersionOneContract()
    {
        var settings = AppSettings.Default;

        Assert.Equal(1, settings.SchemaVersion);
        Assert.False(settings.StartWithWindows);
        Assert.Equal(AppTheme.System, settings.Theme);
        Assert.Equal("Ctrl+Alt+I", settings.InteractionHotkey);
        Assert.True(settings.SuspendOnFullscreen);
        Assert.False(settings.SuspendOnMaximized);
        Assert.True(settings.SuspendOnRemoteDesktop);
        Assert.True(settings.SuspendOnSessionLock);
        Assert.True(settings.SuspendOnDisplayOff);
        Assert.True(settings.SuspendOnSystemSleep);
        Assert.Equal(30, settings.BatteryTargetFps);
        Assert.Equal(15, settings.BatterySaverTargetFps);
        Assert.Equal(IncompatibleThrottleBehavior.Continue, settings.IncompatibleThrottle);
        Assert.Null(settings.FallbackWallpaper);
        Assert.Equal("#101014", settings.FallbackColor);
        Assert.Equal(FitMode.Cover, settings.DefaultFit);
        Assert.Equal(30, settings.DefaultTargetFps);
        Assert.False(settings.DefaultAudioEnabled);
        Assert.Equal(0, settings.DefaultVolumePercent);
        Assert.False(settings.DefaultInteractionEnabled);
        Assert.Null(settings.ManagementWindow);
    }

    [Fact]
    public void PersistedState_DefaultsUseOneConsistencyBoundary()
    {
        var state = PersistedState.Default;

        Assert.Equal(1, state.SchemaVersion);
        Assert.Empty(state.Library);
        Assert.Empty(state.Assignments);
        Assert.Empty(state.Groups);
        Assert.Null(state.AudioOwner);
    }

    [Fact]
    public void ValidatingConstructors_RejectInvalidBoundaries()
    {
        Assert.Throws<ArgumentException>(() => new DisplayGroupId(Guid.Empty));
        Assert.Throws<ArgumentException>(() => new WindowPlacementSettings(0, 0, 0, 100, false));
        Assert.Throws<ArgumentException>(() => new WindowPlacementSettings(double.NaN, 0, 100, 100, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Default with { DefaultTargetFps = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => AppSettings.Default with { DefaultVolumePercent = 101 });
        Assert.Throws<ArgumentException>(() => AppSettings.Default with { FallbackColor = "#fff" });
        Assert.Throws<ArgumentException>(() => AppSettings.Default with { InteractionHotkey = " " });
        Assert.Throws<ArgumentException>(() => AppSettings.Default with { FallbackWallpaper = default(WallpaperId) });
        Assert.Throws<ArgumentException>(() => new SourceValidation(
            SourceValidationStatus.Available,
            null,
            " ",
            DateTimeOffset.UtcNow));
        Assert.Throws<ArgumentException>(() => new VideoMetadata(0, 1080, TimeSpan.FromSeconds(1), 30, "h264", true));
        Assert.Throws<ArgumentException>(() => new VideoMetadata(1920, 1080, TimeSpan.Zero, 30, "h264", true));
        Assert.Throws<ArgumentException>(() => new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(1), double.PositiveInfinity, "h264", true));
        Assert.Throws<ArgumentException>(() => new WallpaperLibraryItem(
            PersistenceTestData.SolidDefinition(),
            "relative\\thumb.png",
            null,
            PersistenceTestData.AvailableValidation()));
    }

    [Fact]
    public void PersistenceLoadResult_RejectsInvalidPublicOutcomes()
    {
        Assert.Throws<ArgumentNullException>(() => new PersistenceLoadResult<string>(null!, PersistenceLoadSource.Primary, null));
        Assert.Throws<ArgumentException>(() => new PersistenceLoadResult<string>("value", (PersistenceLoadSource)999, null));
        Assert.Throws<ArgumentException>(() => new PersistenceLoadResult<string>("value", PersistenceLoadSource.Primary, " "));
    }

    [Theory]
    [InlineData("{\"value\":\"not-a-guid\"}")]
    [InlineData("{\"value\":42}")]
    public void WallpaperIdJsonConverter_InvalidValueAlwaysThrowsJsonException(string json)
    {
        Assert.Throws<JsonException>(() => ReadWallpaperId(json));
    }

    private static WallpaperId ReadWallpaperId(string json)
    {
        var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(json));
        Assert.True(reader.Read());
        return new WallpaperIdJsonConverter().Read(ref reader, typeof(WallpaperId), PersistenceJsonContext.Default.Options);
    }

    [Fact]
    public void Settings_RoundTripsThroughSourceGeneratedMetadata()
    {
        var expected = AppSettings.Default with
        {
            StartWithWindows = true,
            Theme = AppTheme.Dark,
            FallbackWallpaper = WallpaperId.New(),
            ManagementWindow = new WindowPlacementSettings(-120.5, 40, 900, 700, true),
        };

        var json = JsonSerializer.Serialize(expected, PersistenceJsonContext.Default.AppSettings);
        var actual = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.AppSettings);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains(Environment.NewLine, json);
    }

    [Fact]
    public void State_RoundTripsAllWallpaperSourceSubtypes()
    {
        var solid = PersistenceTestData.SolidDefinition();
        var video = PersistenceTestData.VideoDefinition(audioEnabled: true);
        var web = PersistenceTestData.WebDefinition();
        var state = new PersistedState(
            1,
            [
                new WallpaperLibraryItem(solid, null, null, PersistenceTestData.AvailableValidation()),
                new WallpaperLibraryItem(video, Path.Combine(Path.GetTempPath(), "video.png"), new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(12), 29.97, "h264", true), PersistenceTestData.AvailableValidation()),
                new WallpaperLibraryItem(web, null, null, PersistenceTestData.AvailableValidation()),
            ],
            [],
            [],
            null);

        var json = JsonSerializer.Serialize(state, PersistenceJsonContext.Default.PersistedState);
        var actual = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.PersistedState);

        Assert.NotNull(actual);
        Assert.Equal(3, actual.Library.Count);
        Assert.Equal("#112233", Assert.IsType<SolidColorSource>(actual.Library[0].Definition.Source).HexColor);
        Assert.Equal(PersistenceTestData.VideoPath, Assert.IsType<VideoSource>(actual.Library[1].Definition.Source).FilePath);
        var actualWeb = Assert.IsType<WebSource>(actual.Library[2].Definition.Source);
        Assert.Equal(PersistenceTestData.WebRoot, actualWeb.DirectoryPath);
        Assert.Equal("index.html", actualWeb.EntryPoint);
        Assert.Contains("\"$kind\": \"solid\"", json);
        Assert.Contains("\"$kind\": \"video\"", json);
        Assert.Contains("\"$kind\": \"web\"", json);
    }

    [Fact]
    public async Task SettingsStore_UsesExactPrimaryAndBackupNamesAndRoundTrips()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VibeWallpaper.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SettingsStore(directory);
            await store.SaveAsync(AppSettings.Default, TestContext.Current.CancellationToken);
            await store.SaveAsync(AppSettings.Default with { Theme = AppTheme.Light }, TestContext.Current.CancellationToken);

            var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(AppTheme.Light, loaded.Value.Theme);
            Assert.True(File.Exists(Path.Combine(directory, "settings.json")));
            Assert.True(File.Exists(Path.Combine(directory, "settings.backup.json")));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SettingsStore_LoadsLegacyVersionInMemory()
    {
        var directory = Path.Combine(Path.GetTempPath(), "VibeWallpaper.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var primary = Path.Combine(directory, "settings.json");
            const string legacy = "{\"schemaVersion\":0,\"startWithWindows\":true,\"theme\":0,\"interactionHotkey\":\"Alt+V\"}";
            await File.WriteAllTextAsync(primary, legacy, TestContext.Current.CancellationToken);

            var loaded = await new SettingsStore(directory).LoadAsync(TestContext.Current.CancellationToken);

            Assert.Equal(PersistenceLoadSource.Migrated, loaded.Source);
            Assert.True(loaded.Value.StartWithWindows);
            Assert.Equal("Alt+V", loaded.Value.InteractionHotkey);
            Assert.Equal(legacy, await File.ReadAllTextAsync(primary, TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }
}

internal static class PersistenceTestData
{
    public static string VideoPath { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vibe-source.mp4"));
    public static string WebRoot { get; } = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vibe-web"));

    public static WallpaperDefinition SolidDefinition(WallpaperId? id = null) => new(
        id ?? WallpaperId.New(),
        "Solid",
        new SolidColorSource("#112233"),
        FitMode.Cover,
        30,
        false,
        false,
        0,
        false);

    public static WallpaperDefinition VideoDefinition(WallpaperId? id = null, bool audioEnabled = false) => new(
        id ?? WallpaperId.New(),
        "Video",
        new VideoSource(VideoPath),
        FitMode.Contain,
        30,
        false,
        audioEnabled,
        audioEnabled ? 25 : 0,
        false);

    public static WallpaperDefinition WebDefinition(WallpaperId? id = null) => new(
        id ?? WallpaperId.New(),
        "Web",
        new WebSource(WebRoot, "index.html"),
        FitMode.Stretch,
        60,
        true,
        false,
        0,
        true);

    public static SourceValidation AvailableValidation() => new(
        SourceValidationStatus.Available,
        new SourceStamp(12, DateTimeOffset.UtcNow, "abc"),
        null,
        DateTimeOffset.UtcNow);

    public static PersistedMonitorReference Monitor(string key) => new(
        new MonitorIdentity(key),
        new MonitorIdentityEvidence(
            1,
            0,
            0,
            null,
            null,
            null,
            null,
            null,
            null,
            key,
            new DisplayViewport(0, 0, 1920, 1080)));
}
