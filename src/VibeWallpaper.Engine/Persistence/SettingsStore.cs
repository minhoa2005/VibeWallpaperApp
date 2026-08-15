using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Persistence.Migrations;

namespace VibeWallpaper.Engine.Persistence;

public sealed class SettingsStore : ISettingsStore
{
    private readonly AtomicJsonStore<AppSettings> _store;

    public SettingsStore()
        : this(DefaultDirectory())
    {
    }

    public SettingsStore(string directory, IAtomicFileSystem? fileSystem = null)
        : this(
            Path.Combine(Path.GetFullPath(directory), "settings.json"),
            Path.Combine(Path.GetFullPath(directory), "settings.backup.json"),
            fileSystem ?? new PhysicalAtomicFileSystem())
    {
    }

    public SettingsStore(string primaryPath, string backupPath, IAtomicFileSystem fileSystem)
    {
        _store = new AtomicJsonStore<AppSettings>(
            primaryPath,
            backupPath,
            fileSystem,
            PersistenceJsonContext.Default.AppSettings,
            static () => AppSettings.Default,
            Validate,
            [new SettingsV0ToV1Migration()]);
    }

    public Task<PersistenceLoadResult<AppSettings>> LoadAsync(CancellationToken cancellationToken) =>
        _store.LoadAsync(cancellationToken);

    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) =>
        _store.SaveAsync(settings, cancellationToken);

    private static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != 1)
            throw new PersistenceValidationException("settings.invalid_schema_version");
    }

    private static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VibeWallpaper");
}
