using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Persistence;

namespace VibeWallpaper.Tests.Persistence;

public sealed partial class StateStoreTests
{
    [Fact]
    public void ValidateAndNormalize_RejectsEverySemanticIntegrityViolationWithStableCode()
    {
        var wallpaper = PersistenceTestData.SolidDefinition();
        var otherWallpaper = PersistenceTestData.SolidDefinition();
        var monitorA = PersistenceTestData.Monitor("A");
        var monitorB = PersistenceTestData.Monitor("B");
        var groupId = DisplayGroupId.New();
        var otherGroupId = DisplayGroupId.New();
        var assignmentA = Assignment(monitorA, wallpaper.Id);
        var assignmentB = Assignment(monitorB, wallpaper.Id);
        var validLibrary = Library(wallpaper, otherWallpaper);

        var cases = new (string Code, PersistedState State)[]
        {
            (StateValidationCodes.DuplicateWallpaperId, State([Item(wallpaper), Item(wallpaper)], [assignmentA], [])),
            (StateValidationCodes.DuplicateAssignmentOutput, State(validLibrary, [assignmentA, assignmentA], [])),
            (StateValidationCodes.DuplicateGroupId, State(validLibrary, [assignmentA], [Group(groupId, wallpaper.Id, [monitorA.Identity]), Group(groupId, wallpaper.Id, [monitorA.Identity])])),
            (StateValidationCodes.AssignmentWallpaperMissing, State([Item(otherWallpaper)], [assignmentA], [])),
            (StateValidationCodes.GroupIndependent, State(validLibrary, [assignmentA], [Group(groupId, wallpaper.Id, [monitorA.Identity], DisplayMode.Independent)])),
            (StateValidationCodes.GroupMembersEmpty, State(validLibrary, [assignmentA], [Group(groupId, wallpaper.Id, [])])),
            (StateValidationCodes.GroupMemberDuplicate, State(validLibrary, [assignmentA], [Group(groupId, wallpaper.Id, [monitorA.Identity, monitorA.Identity])])),
            (StateValidationCodes.GroupMemberMissingAssignment, State(validLibrary, [assignmentA], [Group(groupId, wallpaper.Id, [monitorB.Identity])])),
            (StateValidationCodes.IndependentAssignmentHasGroup, State(validLibrary, [Assignment(monitorA, wallpaper.Id, DisplayMode.Independent, groupId)], [])),
            (StateValidationCodes.GroupedAssignmentMissingGroup, State(validLibrary, [Assignment(monitorA, wallpaper.Id, DisplayMode.Duplicate)], [])),
            (StateValidationCodes.AssignmentGroupMissing, State(validLibrary, [Assignment(monitorA, wallpaper.Id, DisplayMode.Duplicate, groupId)], [])),
            (StateValidationCodes.GroupMismatch, State(validLibrary, [Assignment(monitorA, wallpaper.Id, DisplayMode.Span, groupId)], [Group(groupId, wallpaper.Id, [monitorA.Identity], DisplayMode.Duplicate)])),
            (StateValidationCodes.GroupMismatch, State(validLibrary, [Assignment(monitorA, wallpaper.Id, DisplayMode.Duplicate, otherGroupId)], [Group(groupId, wallpaper.Id, [monitorA.Identity])])),
            (StateValidationCodes.GroupMismatch, State(validLibrary, [Assignment(monitorA, otherWallpaper.Id, DisplayMode.Duplicate, groupId)], [Group(groupId, wallpaper.Id, [monitorA.Identity])])),
            (StateValidationCodes.GroupMemberMissingAssignment, State(validLibrary, [Assignment(monitorA, wallpaper.Id, DisplayMode.Duplicate, groupId)], [Group(groupId, wallpaper.Id, [monitorA.Identity, monitorB.Identity])])),
        };

        foreach (var item in cases)
        {
            var exception = Assert.Throws<PersistenceValidationException>(() => PersistedStateValidator.ValidateAndNormalize(item.State));
            Assert.Equal(item.Code, exception.DiagnosticCode);
        }
    }

    [Fact]
    public void ValidateAndNormalize_RejectsInvalidFallbackWallpaper()
    {
        var solid = PersistenceTestData.SolidDefinition();
        var video = PersistenceTestData.VideoDefinition();
        var state = State(Library(solid, video), [], []);

        var missing = Assert.Throws<PersistenceValidationException>(() =>
            PersistedStateValidator.ValidateAndNormalize(state, WallpaperId.New()));
        var nonSolid = Assert.Throws<PersistenceValidationException>(() =>
            PersistedStateValidator.ValidateAndNormalize(state, video.Id));

        Assert.Equal(StateValidationCodes.FallbackWallpaperMissing, missing.DiagnosticCode);
        Assert.Equal(StateValidationCodes.FallbackWallpaperNotSolid, nonSolid.DiagnosticCode);
    }

    [Fact]
    public void ValidateAndNormalize_RejectsInvalidAudioOwnerButAllowsDisconnectedAssignedOwner()
    {
        var solid = PersistenceTestData.SolidDefinition();
        var silentVideo = PersistenceTestData.VideoDefinition(audioEnabled: false);
        var audibleVideo = PersistenceTestData.VideoDefinition(audioEnabled: true);
        var owner = PersistenceTestData.Monitor("disconnected-owner");

        var noAssignment = State(Library(solid), [], [], owner.Identity);
        var solidAssignment = State(Library(solid), [Assignment(owner, solid.Id)], [], owner.Identity);
        var silentAssignment = State(Library(silentVideo), [Assignment(owner, silentVideo.Id)], [], owner.Identity);
        var validDisconnected = State(Library(audibleVideo), [Assignment(owner, audibleVideo.Id)], [], owner.Identity);

        Assert.Equal(StateValidationCodes.AudioOwnerAssignmentMissing,
            Assert.Throws<PersistenceValidationException>(() => PersistedStateValidator.ValidateAndNormalize(noAssignment)).DiagnosticCode);
        Assert.Equal(StateValidationCodes.AudioOwnerNotVideo,
            Assert.Throws<PersistenceValidationException>(() => PersistedStateValidator.ValidateAndNormalize(solidAssignment)).DiagnosticCode);
        Assert.Equal(StateValidationCodes.AudioOwnerAudioDisabled,
            Assert.Throws<PersistenceValidationException>(() => PersistedStateValidator.ValidateAndNormalize(silentAssignment)).DiagnosticCode);
        Assert.Same(validDisconnected, PersistedStateValidator.ValidateAndNormalize(validDisconnected));
    }

    [Fact]
    public void ValidateAndNormalize_SortsGroupMembersOnCopyWithoutMutatingCallerList()
    {
        var wallpaper = PersistenceTestData.SolidDefinition();
        var monitorA = PersistenceTestData.Monitor("A");
        var monitorB = PersistenceTestData.Monitor("B");
        var groupId = DisplayGroupId.New();
        var callerMembers = new List<MonitorIdentity> { monitorB.Identity, monitorA.Identity };
        var assignments = new[]
        {
            Assignment(monitorA, wallpaper.Id, DisplayMode.Span, groupId),
            Assignment(monitorB, wallpaper.Id, DisplayMode.Span, groupId),
        };
        var state = State(Library(wallpaper), assignments, [Group(groupId, wallpaper.Id, callerMembers, DisplayMode.Span)]);

        var normalized = PersistedStateValidator.ValidateAndNormalize(state);

        Assert.Equal(["A", "B"], normalized.Groups[0].Members.Select(static member => member.Key));
        Assert.Equal(["B", "A"], callerMembers.Select(static member => member.Key));
        Assert.NotSame(state, normalized);
    }

    [Fact]
    public void PersistedMonitorReference_RejectsInvalidNestedEvidence()
    {
        var valid = PersistenceTestData.Monitor("A");
        Assert.Throws<ArgumentException>(() => new PersistedMonitorReference(
            valid.Identity,
            valid.Evidence with { FriendlyName = " " }));
        Assert.Throws<ArgumentNullException>(() => new PersistedMonitorReference(
            valid.Identity,
            valid.Evidence with { LastBounds = null! }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValidateAndNormalize_RejectsBypassedInvalidMonitorEvidence(bool missingEvidence)
    {
        var wallpaper = PersistenceTestData.SolidDefinition();
        var valid = PersistenceTestData.Monitor("A");
        var invalidReference = CreateBypassedMonitorReference(
            valid.Identity,
            missingEvidence ? null : valid.Evidence with { FriendlyName = " " });
        var state = State(
            Library(wallpaper),
            [new WallpaperAssignment(invalidReference, wallpaper.Id, DisplayMode.Independent, FitMode.Cover, 30, 0, null)],
            []);

        var exception = Assert.Throws<PersistenceValidationException>(() =>
            PersistedStateValidator.ValidateAndNormalize(state));

        Assert.Equal("state.invalid_monitor_evidence", exception.DiagnosticCode);
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData(42)]
    public async Task LoadAsync_InvalidWallpaperIdTokenFallsBackToKnownGoodBackup(object invalidValue)
    {
        var wallpaper = PersistenceTestData.SolidDefinition();
        var validState = State(Library(wallpaper), [], []);
        var validJson = JsonSerializer.Serialize(validState, PersistenceJsonContext.Default.PersistedState);
        var corrupt = JsonNode.Parse(validJson)!.AsObject();
        corrupt["library"]![0]!["definition"]!["id"]!["value"] = JsonValue.Create(invalidValue);
        await File.WriteAllTextAsync(Path.Combine(_directory, "state.json"), corrupt.ToJsonString(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "state.backup.json"), validJson, TestContext.Current.CancellationToken);

        var loaded = await new StateStore(_directory).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceLoadSource.Backup, loaded.Source);
        Assert.Equal(wallpaper.Id, Assert.Single(loaded.Value.Library).Definition.Id);
        Assert.Equal(PersistenceDiagnosticCodes.PrimaryCorrupt, loaded.DiagnosticCode);
    }

    [Theory]
    [InlineData("friendlyName")]
    [InlineData("lastBounds")]
    public async Task LoadAsync_InvalidNestedMonitorEvidenceFallsBackToKnownGoodBackup(string invalidProperty)
    {
        var wallpaper = PersistenceTestData.SolidDefinition();
        var monitor = PersistenceTestData.Monitor("A");
        var validState = State(
            Library(wallpaper),
            [new WallpaperAssignment(monitor, wallpaper.Id, DisplayMode.Independent, FitMode.Cover, 30, 0, null)],
            []);
        var validJson = JsonSerializer.Serialize(validState, PersistenceJsonContext.Default.PersistedState);
        var corrupt = JsonNode.Parse(validJson)!.AsObject();
        var evidence = corrupt["assignments"]![0]!["monitor"]!["evidence"]!;
        evidence[invalidProperty] = invalidProperty == "friendlyName" ? JsonValue.Create(" ") : null;
        await File.WriteAllTextAsync(Path.Combine(_directory, "state.json"), corrupt.ToJsonString(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(_directory, "state.backup.json"), validJson, TestContext.Current.CancellationToken);

        Assert.ThrowsAny<Exception>(() => JsonSerializer.Deserialize(corrupt, PersistenceJsonContext.Default.PersistedState));
        var loaded = await new StateStore(_directory).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceLoadSource.Backup, loaded.Source);
        Assert.Equal(PersistenceDiagnosticCodes.PrimaryCorrupt, loaded.DiagnosticCode);
    }

    private static WallpaperLibraryItem Item(WallpaperDefinition definition) =>
        new(definition, null, null, PersistenceTestData.AvailableValidation());

    private static IReadOnlyList<WallpaperLibraryItem> Library(params WallpaperDefinition[] definitions) =>
        definitions.Select(Item).ToArray();

    private static WallpaperAssignment Assignment(
        PersistedMonitorReference monitor,
        WallpaperId wallpaper,
        DisplayMode mode = DisplayMode.Independent,
        DisplayGroupId? groupId = null) =>
        new(monitor, wallpaper, mode, FitMode.Cover, 30, 0, groupId);

    private static PersistedDisplayGroup Group(
        DisplayGroupId id,
        WallpaperId wallpaper,
        IReadOnlyList<MonitorIdentity> members,
        DisplayMode mode = DisplayMode.Duplicate) => new(id, mode, wallpaper, members);

    private static PersistedState State(
        IReadOnlyList<WallpaperLibraryItem> library,
        IReadOnlyList<WallpaperAssignment> assignments,
        IReadOnlyList<PersistedDisplayGroup> groups,
        MonitorIdentity? audioOwner = null) => new(1, library, assignments, groups, audioOwner);

    private static PersistedMonitorReference CreateBypassedMonitorReference(
        MonitorIdentity identity,
        MonitorIdentityEvidence? evidence)
    {
        var reference = (PersistedMonitorReference)RuntimeHelpers.GetUninitializedObject(typeof(PersistedMonitorReference));
        typeof(PersistedMonitorReference).GetField("<Identity>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(reference, identity);
        typeof(PersistedMonitorReference).GetField("<Evidence>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(reference, evidence);
        return reference;
    }
}

public sealed partial class StateStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "VibeWallpaper.Tests", Guid.NewGuid().ToString("N"));

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
    public async Task SaveAsync_WritesSingleStateBoundaryWithStableGroupOrdering()
    {
        var wallpaper = PersistenceTestData.SolidDefinition();
        var monitorA = PersistenceTestData.Monitor("A");
        var monitorB = PersistenceTestData.Monitor("B");
        var groupId = DisplayGroupId.New();
        var state = new PersistedState(
            1,
            [new WallpaperLibraryItem(wallpaper, null, null, PersistenceTestData.AvailableValidation())],
            [
                new WallpaperAssignment(monitorA, wallpaper.Id, DisplayMode.Span, FitMode.Cover, 30, 0, groupId),
                new WallpaperAssignment(monitorB, wallpaper.Id, DisplayMode.Span, FitMode.Cover, 30, 0, groupId),
            ],
            [new PersistedDisplayGroup(groupId, DisplayMode.Span, wallpaper.Id, [monitorB.Identity, monitorA.Identity])],
            null);
        var store = new StateStore(_directory);

        await store.SaveAsync(state, TestContext.Current.CancellationToken);
        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        var json = await File.ReadAllTextAsync(Path.Combine(_directory, "state.json"), TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceLoadSource.Primary, loaded.Source);
        Assert.Equal(["A", "B"], loaded.Value.Groups[0].Members.Select(static member => member.Key));
        Assert.True(json.IndexOf("\"key\": \"A\"", StringComparison.Ordinal) < json.LastIndexOf("\"key\": \"B\"", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(_directory, "library.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "assignments.json")));
        Assert.False(File.Exists(Path.Combine(_directory, "groups.json")));
    }

    [Fact]
    public async Task SaveAsync_DoesNotReadWriteOrChangeReferencedSourceContent()
    {
        var source = Path.Combine(_directory, "nguồn video.mp4");
        var bytes = Enumerable.Range(0, 256).Select(static value => (byte)value).ToArray();
        await File.WriteAllBytesAsync(source, bytes, TestContext.Current.CancellationToken);
        var timestamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, timestamp);
        var definition = new WallpaperDefinition(
            WallpaperId.New(), "Source", new VideoSource(source), FitMode.Cover, 30, false, false, 0, false);
        var state = new PersistedState(
            1,
            [new WallpaperLibraryItem(definition, null, null, PersistenceTestData.AvailableValidation())],
            [],
            [],
            null);

        await new StateStore(_directory).SaveAsync(state, TestContext.Current.CancellationToken);

        Assert.Equal(bytes, await File.ReadAllBytesAsync(source, TestContext.Current.CancellationToken));
        Assert.Equal(timestamp, File.GetLastWriteTimeUtc(source));
    }

    [Fact]
    public async Task LoadAsync_NonemptyLegacyStateMigratesRelationshipsWithoutPersisting()
    {
        var primary = Path.Combine(_directory, "state.json");
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
        var legacy = raw.ToJsonString();
        await File.WriteAllTextAsync(primary, legacy, TestContext.Current.CancellationToken);

        var loaded = await new StateStore(_directory).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceLoadSource.Migrated, loaded.Source);
        Assert.Equal(wallpaper.Id, Assert.Single(loaded.Value.Library).Definition.Id);
        Assert.Equal(groupId, Assert.Single(loaded.Value.Assignments).GroupId);
        Assert.Equal(groupId, Assert.Single(loaded.Value.Groups).Id);
        Assert.Equal(legacy, await File.ReadAllTextAsync(primary, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LoadAsync_LegacyStateMissingGroupsAddsEmptyCollectionAndPreservesIndependentAssignment()
    {
        var primary = Path.Combine(_directory, "state.json");
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
        var legacy = raw.ToJsonString();
        await File.WriteAllTextAsync(primary, legacy, TestContext.Current.CancellationToken);

        var loaded = await new StateStore(_directory).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceLoadSource.Migrated, loaded.Source);
        Assert.Empty(loaded.Value.Groups);
        var item = Assert.Single(loaded.Value.Library);
        Assert.Equal(wallpaper.Id, item.Definition.Id);
        Assert.Equal(PersistenceTestData.VideoPath, Assert.IsType<VideoSource>(item.Definition.Source).FilePath);
        var assignment = Assert.Single(loaded.Value.Assignments);
        Assert.Equal("legacy-independent", assignment.Monitor.Identity.Key);
        Assert.Equal(wallpaper.Id, assignment.Wallpaper);
        Assert.Equal(DisplayMode.Independent, assignment.Mode);
        Assert.Null(assignment.GroupId);
        Assert.Equal(legacy, await File.ReadAllTextAsync(primary, TestContext.Current.CancellationToken));
    }
}
