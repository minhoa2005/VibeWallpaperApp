using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Persistence;

namespace VibeWallpaper.Tests.Persistence;

public sealed class AtomicJsonStoreTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "VibeWallpaper.Tests",
        Guid.NewGuid().ToString("N"));

    private string Primary => Path.Combine(_directory, "document.json");
    private string Backup => Path.Combine(_directory, "document.backup.json");

    public ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        return ValueTask.CompletedTask;
    }

    [Fact]
    public async Task LoadAsync_CorruptPrimary_LoadsKnownGoodBackup()
    {
        await File.WriteAllTextAsync(Primary, "{broken", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Backup,
            JsonSerializer.Serialize(new TestDocument(1, "safe"), TestJsonContext.Default.TestDocument),
            TestContext.Current.CancellationToken);

        var store = new AtomicJsonStore<TestDocument>(
            Primary,
            Backup,
            new PhysicalAtomicFileSystem(),
            TestJsonContext.Default.TestDocument,
            () => new TestDocument(1, "default"),
            document =>
            {
                if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Value))
                {
                    throw new InvalidDataException("Invalid test document.");
                }
            });

        var result = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("safe", result.Value.Value);
        Assert.Equal(PersistenceLoadSource.Backup, result.Source);
        Assert.Equal(PersistenceDiagnosticCodes.PrimaryCorrupt, result.DiagnosticCode);
    }

    [Fact]
    public async Task LoadAsync_PrefersValidPrimaryAndDefaultsWhenNeitherDocumentExists()
    {
        var store = CreateStore(new PhysicalAtomicFileSystem());
        var missing = await store.LoadAsync(TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Primary, Serialize(new TestDocument(1, "primary")), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Backup, Serialize(new TestDocument(1, "backup")), TestContext.Current.CancellationToken);

        var loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("default", missing.Value.Value);
        Assert.Equal(PersistenceLoadSource.Defaults, missing.Source);
        Assert.Equal(PersistenceDiagnosticCodes.DocumentsMissing, missing.DiagnosticCode);
        Assert.Equal("primary", loaded.Value.Value);
        Assert.Equal(PersistenceLoadSource.Primary, loaded.Source);
    }

    [Fact]
    public async Task LoadAsync_CorruptBoth_ReturnsDefaults()
    {
        await File.WriteAllTextAsync(Primary, "{broken", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Backup, "{also-broken", TestContext.Current.CancellationToken);

        var result = await CreateStore(new PhysicalAtomicFileSystem()).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal("default", result.Value.Value);
        Assert.Equal(PersistenceLoadSource.Defaults, result.Source);
        Assert.Equal(PersistenceDiagnosticCodes.BackupCorrupt, result.DiagnosticCode);
    }

    [Fact]
    public async Task LoadAsync_CorruptPrimaryAndMissingBackup_RetainsPrimaryFailureDiagnostic()
    {
        await File.WriteAllTextAsync(Primary, "{broken", TestContext.Current.CancellationToken);

        var result = await CreateStore(new PhysicalAtomicFileSystem()).LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(PersistenceLoadSource.Defaults, result.Source);
        Assert.Equal(PersistenceDiagnosticCodes.PrimaryCorrupt, result.DiagnosticCode);
    }

    [Fact]
    public async Task SaveAsync_FirstSaveMovesCompleteValidatedTempIntoPrimary()
    {
        var store = CreateStore(new PhysicalAtomicFileSystem());

        await store.SaveAsync(new TestDocument(1, "first"), TestContext.Current.CancellationToken);

        Assert.Equal("first", Deserialize(await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken)).Value);
        Assert.False(File.Exists(Backup));
        Assert.Empty(OwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_ValidPrimaryRotatesExactKnownGoodBackup()
    {
        var store = CreateStore(new PhysicalAtomicFileSystem());
        await store.SaveAsync(new TestDocument(1, "old"), TestContext.Current.CancellationToken);

        await store.SaveAsync(new TestDocument(1, "new"), TestContext.Current.CancellationToken);

        Assert.Equal("new", Deserialize(await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken)).Value);
        Assert.Equal("old", Deserialize(await File.ReadAllTextAsync(Backup, TestContext.Current.CancellationToken)).Value);
        Assert.Empty(OwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_CorruptPrimaryPreservesInvalidAndDoesNotPoisonBackup()
    {
        var store = CreateStore(new PhysicalAtomicFileSystem());
        var sentinel = Path.Combine(_directory, "unrelated.sentinel");
        await File.WriteAllTextAsync(sentinel, "do-not-touch", TestContext.Current.CancellationToken);
        await store.SaveAsync(new TestDocument(1, "known-good"), TestContext.Current.CancellationToken);
        await store.SaveAsync(new TestDocument(1, "later"), TestContext.Current.CancellationToken);
        var backupBefore = await File.ReadAllTextAsync(Backup, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Primary, "{broken", TestContext.Current.CancellationToken);

        await store.SaveAsync(new TestDocument(1, "repaired"), TestContext.Current.CancellationToken);

        Assert.Equal("repaired", Deserialize(await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken)).Value);
        Assert.Equal(backupBefore, await File.ReadAllTextAsync(Backup, TestContext.Current.CancellationToken));
        var invalid = Assert.Single(Directory.GetFiles(_directory, "document.json.*.invalid"));
        Assert.Equal("{broken", await File.ReadAllTextAsync(invalid, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(sentinel));
        Assert.Equal("do-not-touch", await File.ReadAllTextAsync(sentinel, TestContext.Current.CancellationToken));
        Assert.Empty(OwnedTemps());
    }

    [Theory]
    [InlineData(FaultStage.TempWrite)]
    [InlineData(FaultStage.Flush)]
    [InlineData(FaultStage.TempValidation)]
    [InlineData(FaultStage.PrimaryValidation)]
    [InlineData(FaultStage.Replace)]
    public async Task SaveAsync_InjectedStageFailurePreservesPriorConsistencyBoundary(FaultStage stage)
    {
        var physical = new PhysicalAtomicFileSystem();
        await CreateStore(physical).SaveAsync(new TestDocument(1, "stable"), TestContext.Current.CancellationToken);
        var faulting = new FaultingAtomicFileSystem(physical, Primary, stage);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateStore(faulting).SaveAsync(new TestDocument(1, "rejected"), TestContext.Current.CancellationToken));

        Assert.Equal("stable", Deserialize(await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken)).Value);
        Assert.Empty(OwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_FirstMoveFailureLeavesNoPartialPrimaryOrOwnedTemp()
    {
        var fileSystem = new FaultingAtomicFileSystem(new PhysicalAtomicFileSystem(), Primary, FaultStage.Move);

        await Assert.ThrowsAsync<IOException>(() =>
            CreateStore(fileSystem).SaveAsync(new TestDocument(1, "rejected"), TestContext.Current.CancellationToken));

        Assert.False(File.Exists(Primary));
        Assert.False(File.Exists(Backup));
        Assert.Empty(OwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_CancellationBeforeWorkLeavesPriorDocumentAndNoTemp()
    {
        var store = CreateStore(new PhysicalAtomicFileSystem());
        await store.SaveAsync(new TestDocument(1, "stable"), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync(new TestDocument(1, "rejected"), cancellation.Token));

        Assert.Equal("stable", Deserialize(await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken)).Value);
        Assert.Empty(OwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_CancellationDuringSerializationDeletesOnlyOwnedTemp()
    {
        var physical = new PhysicalAtomicFileSystem();
        await CreateStore(physical).SaveAsync(new TestDocument(1, "stable"), TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        var fileSystem = new CancelOnWriteAtomicFileSystem(physical, cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateStore(fileSystem).SaveAsync(new TestDocument(1, new string('x', 32_000)), cancellation.Token));

        Assert.Equal("stable", Deserialize(await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken)).Value);
        Assert.Empty(OwnedTemps());
    }

    [Fact]
    public async Task SaveAsync_ConcurrentCallsSerializeAndLeaveOnlyCompleteDocuments()
    {
        var store = CreateStore(new PhysicalAtomicFileSystem());

        await Task.WhenAll(Enumerable.Range(0, 16).Select(index =>
            store.SaveAsync(new TestDocument(1, $"value-{index}"), TestContext.Current.CancellationToken)));

        var primary = Deserialize(await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken));
        var backup = Deserialize(await File.ReadAllTextAsync(Backup, TestContext.Current.CancellationToken));
        Assert.StartsWith("value-", primary.Value);
        Assert.StartsWith("value-", backup.Value);
        Assert.NotEqual(primary.Value, backup.Value);
        Assert.Empty(OwnedTemps());
    }

    [Fact]
    public Task SaveAsync_FuturePrimaryRejectsSaveAndPreservesBothDocuments() =>
        AssertIncompatibleSavePreservesAsync(
            "{\"schemaVersion\":2,\"value\":\"future\"}",
            [],
            PersistenceDiagnosticCodes.UnsupportedSchema);

    [Fact]
    public Task SaveAsync_MigrationGapRejectsSaveAndPreservesBothDocuments() =>
        AssertIncompatibleSavePreservesAsync(
            "{\"schemaVersion\":0,\"value\":\"legacy\"}",
            [],
            PersistenceDiagnosticCodes.MigrationGap);

    [Fact]
    public Task SaveAsync_DuplicateMigrationRejectsSaveAndPreservesBothDocuments() =>
        AssertIncompatibleSavePreservesAsync(
            "{\"schemaVersion\":0,\"value\":\"legacy\"}",
            [new AtomicV0ToV1Migration(), new AtomicV0ToV1Migration()],
            PersistenceDiagnosticCodes.MigrationDuplicate);

    [Fact]
    public Task SaveAsync_FailedMigrationRejectsSaveAndPreservesBothDocuments() =>
        AssertIncompatibleSavePreservesAsync(
            "{\"schemaVersion\":0,\"value\":\"legacy\"}",
            [new AtomicThrowingMigration()],
            PersistenceDiagnosticCodes.MigrationFailed);

    [Theory]
    [InlineData(DeterministicFailure.PartialTempWrite)]
    [InlineData(DeterministicFailure.Flush)]
    [InlineData(DeterministicFailure.TempValidationRead)]
    [InlineData(DeterministicFailure.PrimaryValidationRead)]
    [InlineData(DeterministicFailure.Replace)]
    public async Task SaveAsync_DeterministicStageFaultPreservesFilesAndDeletesOnlyOwnedTemp(DeterministicFailure failure)
    {
        var fake = CreatePopulatedFake(failure);
        var primaryBefore = fake.ReadText(Primary);
        var backupBefore = fake.ReadText(Backup);
        var sentinel = Path.Combine(_directory, "unrelated.sentinel");

        await Assert.ThrowsAnyAsync<Exception>(() =>
            CreateStore(fake).SaveAsync(new TestDocument(1, "rejected"), TestContext.Current.CancellationToken));

        Assert.Equal(primaryBefore, fake.ReadText(Primary));
        Assert.Equal(backupBefore, fake.ReadText(Backup));
        Assert.Equal("do-not-touch", fake.ReadText(sentinel));
        Assert.DoesNotContain(fake.Paths, static path => path.EndsWith(".tmp", StringComparison.Ordinal));
        Assert.All(fake.DeletedPaths, static path => Assert.EndsWith(".tmp", path, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_DeterministicFirstMoveFaultLeavesSentinelAndNoDocumentOrTemp()
    {
        var fake = new DeterministicAtomicFileSystem(DeterministicFailure.FirstSaveMove);
        var sentinel = Path.Combine(_directory, "unrelated.sentinel");
        fake.Seed(sentinel, "do-not-touch");

        await Assert.ThrowsAsync<IOException>(() =>
            CreateStore(fake).SaveAsync(new TestDocument(1, "rejected"), TestContext.Current.CancellationToken));

        Assert.False(fake.FileExists(Primary));
        Assert.False(fake.FileExists(Backup));
        Assert.Equal("do-not-touch", fake.ReadText(sentinel));
        Assert.DoesNotContain(fake.Paths, static path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_DeterministicCorruptPrimaryAtomicReplaceFailurePreservesEveryExistingPath()
    {
        var fake = CreateCorruptFake(DeterministicFailure.Replace);
        var primaryBefore = fake.ReadText(Primary);
        var backupBefore = fake.ReadText(Backup);
        var sentinel = Path.Combine(_directory, "unrelated.sentinel");

        await Assert.ThrowsAsync<IOException>(() =>
            CreateStore(fake).SaveAsync(new TestDocument(1, "rejected"), TestContext.Current.CancellationToken));

        Assert.Equal(primaryBefore, fake.ReadText(Primary));
        Assert.Equal(backupBefore, fake.ReadText(Backup));
        Assert.Equal("do-not-touch", fake.ReadText(sentinel));
        Assert.DoesNotContain(fake.Paths, static path => path.EndsWith(".invalid", StringComparison.Ordinal));
        Assert.DoesNotContain(fake.Paths, static path => path.EndsWith(".tmp", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_DeterministicCleanupFailureNeverTargetsSentinelOrPriorDocuments()
    {
        var fake = CreatePopulatedFake(
            DeterministicFailure.TempValidationRead,
            DeterministicFailure.Cleanup);
        var sentinel = Path.Combine(_directory, "unrelated.sentinel");
        var primaryBefore = fake.ReadText(Primary);
        var backupBefore = fake.ReadText(Backup);

        var exception = await Assert.ThrowsAsync<IOException>(() =>
            CreateStore(fake).SaveAsync(new TestDocument(1, "rejected"), TestContext.Current.CancellationToken));

        Assert.Equal("Injected deterministic cleanup failure.", exception.Message);
        Assert.Equal(primaryBefore, fake.ReadText(Primary));
        Assert.Equal(backupBefore, fake.ReadText(Backup));
        Assert.Equal("do-not-touch", fake.ReadText(sentinel));
        Assert.Single(fake.Paths, static path => path.EndsWith(".tmp", StringComparison.Ordinal));
        var deleteAttempt = Assert.Single(fake.DeleteAttempts);
        Assert.EndsWith(".tmp", deleteAttempt, StringComparison.Ordinal);
    }

    private AtomicJsonStore<TestDocument> CreateStore(
        IAtomicFileSystem fileSystem,
        IReadOnlyList<IPersistenceMigration>? migrations = null) => new(
        Primary,
        Backup,
        fileSystem,
        TestJsonContext.Default.TestDocument,
        () => new TestDocument(1, "default"),
        document =>
        {
            if (document.SchemaVersion != 1 || string.IsNullOrWhiteSpace(document.Value))
                throw new InvalidDataException("Invalid test document.");
        },
        migrations);

    private async Task AssertIncompatibleSavePreservesAsync(
        string primaryContents,
        IReadOnlyList<IPersistenceMigration> migrations,
        string expectedDiagnostic)
    {
        var backupContents = Serialize(new TestDocument(1, "known-good-backup"));
        await File.WriteAllTextAsync(Primary, primaryContents, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Backup, backupContents, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<PersistenceValidationException>(() =>
            CreateStore(new PhysicalAtomicFileSystem(), migrations)
                .SaveAsync(new TestDocument(1, "rejected"), TestContext.Current.CancellationToken));

        Assert.Equal(expectedDiagnostic, exception.DiagnosticCode);
        Assert.Equal(primaryContents, await File.ReadAllTextAsync(Primary, TestContext.Current.CancellationToken));
        Assert.Equal(backupContents, await File.ReadAllTextAsync(Backup, TestContext.Current.CancellationToken));
        Assert.Empty(OwnedTemps());
        Assert.Empty(Directory.GetFiles(_directory, "*.invalid"));
    }

    private DeterministicAtomicFileSystem CreatePopulatedFake(params DeterministicFailure[] failures)
    {
        var fake = new DeterministicAtomicFileSystem(failures);
        fake.Seed(Primary, Serialize(new TestDocument(1, "stable")));
        fake.Seed(Backup, Serialize(new TestDocument(1, "older")));
        fake.Seed(Path.Combine(_directory, "unrelated.sentinel"), "do-not-touch");
        return fake;
    }

    private DeterministicAtomicFileSystem CreateCorruptFake(DeterministicFailure failure)
    {
        var fake = new DeterministicAtomicFileSystem(failure);
        fake.Seed(Primary, "{broken");
        fake.Seed(Backup, Serialize(new TestDocument(1, "known-good-backup")));
        fake.Seed(Path.Combine(_directory, "unrelated.sentinel"), "do-not-touch");
        return fake;
    }

    private string[] OwnedTemps() => Directory.GetFiles(_directory, "document.json.*.tmp");
    private static string Serialize(TestDocument value) => JsonSerializer.Serialize(value, TestJsonContext.Default.TestDocument);
    private static TestDocument Deserialize(string json) => JsonSerializer.Deserialize(json, TestJsonContext.Default.TestDocument)!;
}

public sealed record TestDocument(int SchemaVersion, string Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(TestDocument))]
internal partial class TestJsonContext : JsonSerializerContext;

internal sealed class AtomicV0ToV1Migration : IPersistenceMigration
{
    public int FromVersion => 0;
    public int ToVersion => 1;
    public JsonObject Migrate(JsonObject document)
    {
        var result = (JsonObject)document.DeepClone();
        result["schemaVersion"] = 1;
        return result;
    }
}

internal sealed class AtomicThrowingMigration : IPersistenceMigration
{
    public int FromVersion => 0;
    public int ToVersion => 1;
    public JsonObject Migrate(JsonObject document) => throw new InvalidOperationException("Injected migration failure.");
}

public enum DeterministicFailure
{
    PartialTempWrite,
    Flush,
    TempValidationRead,
    PrimaryValidationRead,
    Replace,
    FirstSaveMove,
    Cleanup,
}

internal sealed class DeterministicAtomicFileSystem : IAtomicFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DeterministicFailure> _failures;

    public DeterministicAtomicFileSystem(params DeterministicFailure[] failures) => _failures = [.. failures];

    public IReadOnlyCollection<string> Paths => _files.Keys;
    public List<string> DeletedPaths { get; } = [];
    public List<string> DeleteAttempts { get; } = [];

    public void Seed(string path, string contents) =>
        _files[Normalize(path)] = System.Text.Encoding.UTF8.GetBytes(contents);

    public string ReadText(string path) => System.Text.Encoding.UTF8.GetString(_files[Normalize(path)]);

    public bool FileExists(string path) => _files.ContainsKey(Normalize(path));

    public Stream OpenRead(string path)
    {
        path = Normalize(path);
        if (_failures.Contains(DeterministicFailure.TempValidationRead) && path.EndsWith(".tmp", StringComparison.Ordinal))
            throw new IOException("Injected deterministic temp read failure.");
        if (_failures.Contains(DeterministicFailure.PrimaryValidationRead) && path.EndsWith("document.json", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Injected deterministic primary read failure.");
        return new MemoryStream(_files[path], writable: false);
    }

    public void CreateDirectory(string path)
    {
    }

    public Stream CreateNew(string path)
    {
        path = Normalize(path);
        if (_files.ContainsKey(path)) throw new IOException("File already exists.");
        _files[path] = [];
        return new DeterministicWriteStream(
            bytes => _files[path] = bytes,
            _failures.Contains(DeterministicFailure.PartialTempWrite));
    }

    public Task FlushAsync(Stream stream, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return _failures.Contains(DeterministicFailure.Flush)
            ? Task.FromException(new IOException("Injected deterministic flush failure."))
            : Task.CompletedTask;
    }

    public void Move(string sourcePath, string destinationPath)
    {
        sourcePath = Normalize(sourcePath);
        destinationPath = Normalize(destinationPath);
        var sourceIsTemp = sourcePath.EndsWith(".tmp", StringComparison.Ordinal);
        var destinationIsPrimary = destinationPath.EndsWith("document.json", StringComparison.OrdinalIgnoreCase);
        var hasInvalid = _files.Keys.Any(static path => path.EndsWith(".invalid", StringComparison.Ordinal));

        if (_failures.Contains(DeterministicFailure.FirstSaveMove) && sourceIsTemp && destinationIsPrimary && !hasInvalid)
            throw new IOException("Injected deterministic first move failure.");
        if (!_files.Remove(sourcePath, out var bytes)) throw new FileNotFoundException(null, sourcePath);
        if (_files.ContainsKey(destinationPath)) throw new IOException("Destination already exists.");
        _files[destinationPath] = bytes;
    }

    public void Replace(string sourcePath, string destinationPath, string backupPath)
    {
        sourcePath = Normalize(sourcePath);
        destinationPath = Normalize(destinationPath);
        backupPath = Normalize(backupPath);
        if (_failures.Contains(DeterministicFailure.Replace))
            throw new IOException("Injected deterministic replace failure.");
        var replacement = _files[sourcePath];
        var previous = _files[destinationPath];
        _files[backupPath] = previous;
        _files[destinationPath] = replacement;
        _files.Remove(sourcePath);
    }

    public void DeleteFile(string path)
    {
        path = Normalize(path);
        DeleteAttempts.Add(path);
        if (_failures.Contains(DeterministicFailure.Cleanup))
            throw new IOException("Injected deterministic cleanup failure.");
        _files.Remove(path);
        DeletedPaths.Add(path);
    }

    private static string Normalize(string path) => Path.GetFullPath(path);
}

internal sealed class DeterministicWriteStream(Action<byte[]> commit, bool failAfterPartialWrite) : Stream
{
    private readonly MemoryStream _inner = new();
    private bool _failed;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => commit(_inner.ToArray());
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (failAfterPartialWrite && !_failed)
        {
            _failed = true;
            var partial = buffer[..Math.Min(5, buffer.Length)];
            _inner.Write(partial.Span);
            commit(_inner.ToArray());
            return ValueTask.FromException(new IOException("Injected deterministic partial write failure."));
        }

        _inner.Write(buffer.Span);
        commit(_inner.ToArray());
        return ValueTask.CompletedTask;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            commit(_inner.ToArray());
            _inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override ValueTask DisposeAsync()
    {
        commit(_inner.ToArray());
        return _inner.DisposeAsync();
    }
}

public enum FaultStage
{
    TempWrite,
    Flush,
    TempValidation,
    PrimaryValidation,
    Move,
    Replace,
}

internal sealed class FaultingAtomicFileSystem(
    IAtomicFileSystem inner,
    string primaryPath,
    FaultStage stage) : IAtomicFileSystem
{
    public bool FileExists(string path) => inner.FileExists(path);
    public Stream OpenRead(string path)
    {
        if (stage == FaultStage.TempValidation && path.EndsWith(".tmp", StringComparison.Ordinal)) throw new IOException("Injected temp validation failure.");
        if (stage == FaultStage.PrimaryValidation && string.Equals(path, primaryPath, StringComparison.OrdinalIgnoreCase)) throw new IOException("Injected primary validation failure.");
        return inner.OpenRead(path);
    }

    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public Stream CreateNew(string path)
    {
        if (stage == FaultStage.TempWrite) throw new IOException("Injected write failure.");
        return inner.CreateNew(path);
    }

    public Task FlushAsync(Stream stream, CancellationToken cancellationToken) =>
        stage == FaultStage.Flush ? Task.FromException(new IOException("Injected flush failure.")) : inner.FlushAsync(stream, cancellationToken);

    public void Move(string sourcePath, string destinationPath)
    {
        if (stage == FaultStage.Move) throw new IOException("Injected move failure.");
        inner.Move(sourcePath, destinationPath);
    }

    public void Replace(string sourcePath, string destinationPath, string backupPath)
    {
        if (stage == FaultStage.Replace) throw new IOException("Injected replace failure.");
        inner.Replace(sourcePath, destinationPath, backupPath);
    }

    public void DeleteFile(string path) => inner.DeleteFile(path);
}

internal sealed class CancelOnWriteAtomicFileSystem(IAtomicFileSystem inner, CancellationTokenSource cancellation) : IAtomicFileSystem
{
    public bool FileExists(string path) => inner.FileExists(path);
    public Stream OpenRead(string path) => inner.OpenRead(path);
    public void CreateDirectory(string path) => inner.CreateDirectory(path);
    public Stream CreateNew(string path) => new CancelOnWriteStream(inner.CreateNew(path), cancellation);
    public Task FlushAsync(Stream stream, CancellationToken cancellationToken) => inner.FlushAsync(((CancelOnWriteStream)stream).Inner, cancellationToken);
    public void Move(string sourcePath, string destinationPath) => inner.Move(sourcePath, destinationPath);
    public void Replace(string sourcePath, string destinationPath, string backupPath) => inner.Replace(sourcePath, destinationPath, backupPath);
    public void DeleteFile(string path) => inner.DeleteFile(path);
}

internal sealed class CancelOnWriteStream : Stream
{
    private readonly CancellationTokenSource _cancellation;

    public CancelOnWriteStream(Stream inner, CancellationTokenSource cancellation)
    {
        Inner = inner;
        _cancellation = cancellation;
    }

    public Stream Inner { get; }
    public override bool CanRead => Inner.CanRead;
    public override bool CanSeek => Inner.CanSeek;
    public override bool CanWrite => Inner.CanWrite;
    public override long Length => Inner.Length;
    public override long Position { get => Inner.Position; set => Inner.Position = value; }
    public override void Flush() => Inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => Inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => Inner.Seek(offset, origin);
    public override void SetLength(long value) => Inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count)
    {
        _cancellation.Cancel();
        throw new OperationCanceledException(_cancellation.Token);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        _cancellation.Cancel();
        return ValueTask.FromCanceled(_cancellation.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Inner.Dispose();
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        await Inner.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
