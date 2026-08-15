using System.Text.Json;
using System.Text.Json.Nodes;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Persistence;
using VibeWallpaper.Engine.Persistence.Migrations;

namespace VibeWallpaper.Tests.Persistence;

public sealed class PersistenceMigrationTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VibeWallpaper.Tests", Guid.NewGuid().ToString("N"));
    private string Primary => Path.Combine(_directory, "document.json");
    private string Backup => Path.Combine(_directory, "document.backup.json");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LoadAsync_VersionZero_MigratesInMemoryWithoutPersisting()
    {
        const string original = "{\"schemaVersion\":0,\"value\":\"legacy\"}";
        await File.WriteAllTextAsync(Primary, original, TestContext.Current.CancellationToken);
        var store = CreateStore([new TestV0ToV1Migration()]);

        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("legacy", result.Value.Value);
        Assert.Equal(1, result.Value.SchemaVersion);
        Assert.Equal(PersistenceLoadSource.Migrated, result.Source);
        Assert.Equal(original, await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_CurrentVersion_BypassesMigration()
    {
        await File.WriteAllTextAsync(Primary, "{\"schemaVersion\":1,\"value\":\"current\"}", TestContext.Current.CancellationToken);
        var throwingMigration = new ThrowingMigration(0, 1);

        var result = await CreateStore([throwingMigration]).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("current", result.Value.Value);
        Assert.Equal(PersistenceLoadSource.Primary, result.Source);
        Assert.Null(result.DiagnosticCode);
    }

    [Theory]
    [InlineData("{\"schemaVersion\":2,\"value\":\"future\"}", PersistenceDiagnosticCodes.UnsupportedSchema)]
    [InlineData("{\"schemaVersion\":0,\"value\":\"gap\"}", PersistenceDiagnosticCodes.MigrationGap)]
    public async Task LoadAsync_IncompatibleVersion_ReturnsDefaultsAndPreservesDocument(string json, string diagnostic)
    {
        await File.WriteAllTextAsync(Primary, json, TestContext.Current.CancellationToken);

        var result = await CreateStore([]).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceLoadSource.Defaults, result.Source);
        Assert.Equal(diagnostic, result.DiagnosticCode);
        Assert.Equal(json, await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_DuplicateMigrationStep_ReturnsStableDiagnostic()
    {
        await File.WriteAllTextAsync(Primary, "{\"schemaVersion\":0,\"value\":\"legacy\"}", TestContext.Current.CancellationToken);

        var result = await CreateStore([new TestV0ToV1Migration(), new TestV0ToV1Migration()])
            .LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceDiagnosticCodes.MigrationDuplicate, result.DiagnosticCode);
        Assert.Equal(PersistenceLoadSource.Defaults, result.Source);
    }

    [Fact]
    public async Task LoadAsync_FailedMigration_ReturnsStableDiagnostic()
    {
        await File.WriteAllTextAsync(Primary, "{\"schemaVersion\":0,\"value\":\"legacy\"}", TestContext.Current.CancellationToken);

        var result = await CreateStore([new ThrowingMigration(0, 1)])
            .LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceDiagnosticCodes.MigrationFailed, result.DiagnosticCode);
        Assert.Equal(PersistenceLoadSource.Defaults, result.Source);
    }

    [Fact]
    public void SettingsVersionZeroMigration_AddsVersionOneDefaults()
    {
        var raw = JsonNode.Parse("""
            {"schemaVersion":0,"startWithWindows":true,"theme":2,"interactionHotkey":"Alt+V"}
            """)!.AsObject();

        var migrated = new SettingsV0ToV1Migration().Migrate(raw);
        var settings = JsonSerializer.Deserialize(migrated, PersistenceJsonContext.Default.AppSettings);

        Assert.NotNull(settings);
        Assert.Equal(1, settings.SchemaVersion);
        Assert.True(settings.StartWithWindows);
        Assert.Equal("Alt+V", settings.InteractionHotkey);
        Assert.Equal(30, settings.DefaultTargetFps);
        Assert.Equal("#101014", settings.FallbackColor);
    }

    [Fact]
    public void StateVersionZeroMigration_PreservesNonemptyRelationshipsAndAddsAudioDefault()
    {
        var wallpaper = PersistenceTestData.SolidDefinition();
        var monitor = PersistenceTestData.Monitor("legacy-output");
        var groupId = DisplayGroupId.New();
        var currentShape = new PersistedState(
            1,
            [new WallpaperLibraryItem(wallpaper, null, null, PersistenceTestData.AvailableValidation())],
            [new WallpaperAssignment(monitor, wallpaper.Id, DisplayMode.Duplicate, FitMode.Cover, 24, 0, groupId)],
            [new PersistedDisplayGroup(groupId, DisplayMode.Duplicate, wallpaper.Id, [monitor.Identity])],
            null);
        var raw = JsonSerializer.SerializeToNode(currentShape, PersistenceJsonContext.Default.PersistedState)!.AsObject();
        raw["schemaVersion"] = 0;
        raw.Remove("audioOwner");

        var migrated = new StateV0ToV1Migration().Migrate(raw);
        var state = JsonSerializer.Deserialize(migrated, PersistenceJsonContext.Default.PersistedState);

        Assert.NotNull(state);
        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal(wallpaper.Id, Assert.Single(state.Library).Definition.Id);
        var assignment = Assert.Single(state.Assignments);
        Assert.Equal("legacy-output", assignment.Monitor.Identity.Key);
        Assert.Equal(groupId, assignment.GroupId);
        var group = Assert.Single(state.Groups);
        Assert.Equal(groupId, group.Id);
        Assert.Equal(wallpaper.Id, group.Wallpaper);
        Assert.Null(state.AudioOwner);
    }

    [Fact]
    public void StateVersionZeroMigration_MissingGroupsAddsEmptyCollectionAndPreservesIndependentAssignment()
    {
        var wallpaper = PersistenceTestData.VideoDefinition(audioEnabled: false);
        var monitor = PersistenceTestData.Monitor("legacy-independent");
        var currentShape = new PersistedState(
            1,
            [new WallpaperLibraryItem(wallpaper, null, null, PersistenceTestData.AvailableValidation())],
            [new WallpaperAssignment(monitor, wallpaper.Id, DisplayMode.Independent, FitMode.Contain, 25, 0, null)],
            [],
            null);
        var raw = JsonSerializer.SerializeToNode(currentShape, PersistenceJsonContext.Default.PersistedState)!.AsObject();
        raw["schemaVersion"] = 0;
        raw.Remove("groups");
        raw.Remove("audioOwner");

        var migrated = new StateV0ToV1Migration().Migrate(raw);
        var state = JsonSerializer.Deserialize(migrated, PersistenceJsonContext.Default.PersistedState);

        Assert.NotNull(state);
        Assert.Equal(1, state.SchemaVersion);
        Assert.Empty(state.Groups);
        var item = Assert.Single(state.Library);
        Assert.Equal(wallpaper.Id, item.Definition.Id);
        Assert.Equal(PersistenceTestData.VideoPath, Assert.IsType<VideoSource>(item.Definition.Source).FilePath);
        var assignment = Assert.Single(state.Assignments);
        Assert.Equal("legacy-independent", assignment.Monitor.Identity.Key);
        Assert.Equal(wallpaper.Id, assignment.Wallpaper);
        Assert.Equal(DisplayMode.Independent, assignment.Mode);
        Assert.Null(assignment.GroupId);
    }

    private AtomicJsonStore<TestDocument> CreateStore(IReadOnlyList<IPersistenceMigration> migrations) => new(
        Primary,
        Backup,
        new PhysicalAtomicFileSystem(),
        TestJsonContext.Default.TestDocument,
        () => new TestDocument(1, "default"),
        document =>
        {
            if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Value))
                throw new InvalidDataException("Invalid test document.");
        },
        migrations);

    private sealed class TestV0ToV1Migration : IPersistenceMigration
    {
        public int FromVersion => 0;
        public int ToVersion => 1;

        public JsonObject Migrate(JsonObject document)
        {
            var migrated = (JsonObject)document.DeepClone();
            migrated["schemaVersion"] = 1;
            return migrated;
        }
    }

    private sealed class ThrowingMigration(int fromVersion, int toVersion) : IPersistenceMigration
    {
        public int FromVersion { get; } = fromVersion;
        public int ToVersion { get; } = toVersion;
        public JsonObject Migrate(JsonObject document) => throw new InvalidOperationException("Injected migration failure.");
    }
}
