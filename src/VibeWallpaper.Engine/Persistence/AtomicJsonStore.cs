using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Engine.Persistence;

public sealed class AtomicJsonStore<T>
{
    private readonly string _primaryPath;
    private readonly string _backupPath;
    private readonly IAtomicFileSystem _fileSystem;
    private readonly JsonTypeInfo<T> _jsonTypeInfo;
    private readonly Func<T> _createDefaults;
    private readonly Action<T> _validate;
    private readonly IReadOnlyList<IPersistenceMigration> _migrations;
    private readonly SemaphoreSlim _saveGate = new(1, 1);

    public AtomicJsonStore(
        string primaryPath,
        string backupPath,
        IAtomicFileSystem fileSystem,
        JsonTypeInfo<T> jsonTypeInfo,
        Func<T> createDefaults,
        Action<T> validate,
        IReadOnlyList<IPersistenceMigration>? migrations = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(primaryPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        ArgumentNullException.ThrowIfNull(createDefaults);
        ArgumentNullException.ThrowIfNull(validate);

        _primaryPath = Path.GetFullPath(primaryPath);
        _backupPath = Path.GetFullPath(backupPath);
        _fileSystem = fileSystem;
        _jsonTypeInfo = jsonTypeInfo;
        _createDefaults = createDefaults;
        _validate = validate;
        _migrations = migrations ?? [];
    }

    public async Task<PersistenceLoadResult<T>> LoadAsync(CancellationToken cancellationToken)
    {
        var primary = await TryReadAsync(_primaryPath, cancellationToken).ConfigureAwait(false);
        if (primary.Success)
        {
            return new PersistenceLoadResult<T>(
                primary.Value!,
                primary.Migrated ? PersistenceLoadSource.Migrated : PersistenceLoadSource.Primary,
                null);
        }

        var backup = await TryReadAsync(_backupPath, cancellationToken).ConfigureAwait(false);
        if (backup.Success)
        {
            return new PersistenceLoadResult<T>(
                backup.Value!,
                backup.Migrated ? PersistenceLoadSource.Migrated : PersistenceLoadSource.Backup,
                primary.Missing ? PersistenceDiagnosticCodes.DocumentsMissing : primary.DiagnosticCode);
        }

        return new PersistenceLoadResult<T>(
            _createDefaults(),
            PersistenceLoadSource.Defaults,
            ChooseDefaultsDiagnostic(primary, backup));
    }

    public async Task SaveAsync(T value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(value);
        await _saveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? tempPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _validate(value);

            var directory = Path.GetDirectoryName(_primaryPath)
                ?? throw new InvalidOperationException("Primary path requires a directory.");
            _fileSystem.CreateDirectory(directory);
            tempPath = Path.Combine(
                directory,
                $"{Path.GetFileName(_primaryPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = _fileSystem.CreateNew(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, value, _jsonTypeInfo, cancellationToken).ConfigureAwait(false);
                await _fileSystem.FlushAsync(stream, cancellationToken).ConfigureAwait(false);
            }

            var temp = await TryReadAsync(tempPath, cancellationToken).ConfigureAwait(false);
            if (!temp.Success || temp.Migrated)
            {
                if (temp.Error is not null)
                    throw new IOException("Unable to validate the serialized temporary document.", temp.Error);
                throw new PersistenceValidationException(temp.DiagnosticCode ?? PersistenceDiagnosticCodes.PrimaryCorrupt);
            }

            var primary = await TryReadAsync(_primaryPath, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (primary.Success)
            {
                _fileSystem.Replace(tempPath, _primaryPath, _backupPath);
                tempPath = null;
                return;
            }

            if (primary.Missing)
            {
                _fileSystem.Move(tempPath, _primaryPath);
                tempPath = null;
                return;
            }

            if (primary.Error is not null)
            {
                throw new IOException("Unable to validate the existing primary document.", primary.Error);
            }

            if (primary.Incompatible)
            {
                throw new PersistenceValidationException(
                    primary.DiagnosticCode ?? PersistenceDiagnosticCodes.UnsupportedSchema);
            }

            var invalidPath = Path.Combine(
                directory,
                $"{Path.GetFileName(_primaryPath)}.{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffffffZ}.{Guid.NewGuid():N}.invalid");
            _fileSystem.Replace(tempPath, _primaryPath, invalidPath);
            tempPath = null;
        }
        finally
        {
            try
            {
                if (tempPath is not null && _fileSystem.FileExists(tempPath))
                    _fileSystem.DeleteFile(tempPath);
            }
            finally
            {
                _saveGate.Release();
            }
        }
    }

    private static string ChooseDefaultsDiagnostic(ReadAttempt primary, ReadAttempt backup)
    {
        if (primary.Missing && backup.Missing) return PersistenceDiagnosticCodes.DocumentsMissing;
        if (primary.Missing) return backup.DiagnosticCode ?? PersistenceDiagnosticCodes.BackupCorrupt;
        if (backup.Missing) return primary.DiagnosticCode ?? PersistenceDiagnosticCodes.PrimaryCorrupt;
        if (primary.Incompatible) return primary.DiagnosticCode!;
        if (backup.Incompatible) return backup.DiagnosticCode!;
        return PersistenceDiagnosticCodes.BackupCorrupt;
    }

    private async Task<ReadAttempt> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!_fileSystem.FileExists(path))
        {
            return new ReadAttempt(false, true, false, false, default, PersistenceDiagnosticCodes.DocumentsMissing, null);
        }

        try
        {
            await using var stream = _fileSystem.OpenRead(path);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (node is not JsonObject document)
            {
                return Corrupt();
            }

            if (!document.TryGetPropertyValue("schemaVersion", out var versionNode) ||
                versionNode is not JsonValue versionValue ||
                !versionValue.TryGetValue<int>(out var version) ||
                version < 0)
            {
                return Corrupt();
            }

            if (version > 1)
            {
                return Incompatible(PersistenceDiagnosticCodes.UnsupportedSchema);
            }

            var migrated = false;
            if (version < 1)
            {
                var duplicate = _migrations
                    .GroupBy(static migration => migration.FromVersion)
                    .Any(static group => group.Count() != 1);
                if (duplicate) return Incompatible(PersistenceDiagnosticCodes.MigrationDuplicate);

                while (version < 1)
                {
                    var migration = _migrations.SingleOrDefault(candidate => candidate.FromVersion == version);
                    if (migration is null || migration.ToVersion != version + 1)
                        return Incompatible(PersistenceDiagnosticCodes.MigrationGap);

                    try
                    {
                        document = migration.Migrate(document);
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        return Incompatible(PersistenceDiagnosticCodes.MigrationFailed);
                    }

                    version = migration.ToVersion;
                    migrated = true;
                }
            }

            T? value;
            try
            {
                value = JsonSerializer.Deserialize(document, _jsonTypeInfo);
            }
            catch (Exception exception) when (exception is FormatException or InvalidOperationException)
            {
                return Corrupt();
            }
            if (value is null)
            {
                return Corrupt();
            }

            _validate(value);
            return new ReadAttempt(true, false, migrated, false, value, null, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or PersistenceValidationException or InvalidDataException)
        {
            return Corrupt();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new ReadAttempt(false, false, false, false, default, PersistenceDiagnosticCodes.PrimaryCorrupt, exception);
        }
    }

    private static ReadAttempt Corrupt() => Failed(PersistenceDiagnosticCodes.PrimaryCorrupt);
    private static ReadAttempt Failed(string diagnosticCode) => new(false, false, false, false, default, diagnosticCode, null);
    private static ReadAttempt Incompatible(string diagnosticCode) => new(false, false, false, true, default, diagnosticCode, null);

    private readonly record struct ReadAttempt(
        bool Success,
        bool Missing,
        bool Migrated,
        bool Incompatible,
        T? Value,
        string? DiagnosticCode,
        Exception? Error);
}
