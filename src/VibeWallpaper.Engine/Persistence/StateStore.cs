using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Persistence.Migrations;

namespace VibeWallpaper.Engine.Persistence;

public sealed class StateStore : IStateStore
{
    private readonly AtomicJsonStore<PersistedState> _store;
    private readonly Func<WallpaperId?> _fallbackWallpaper;

    public StateStore()
        : this(DefaultDirectory())
    {
    }

    public StateStore(
        string directory,
        IAtomicFileSystem? fileSystem = null,
        Func<WallpaperId?>? fallbackWallpaper = null)
        : this(
            Path.Combine(Path.GetFullPath(directory), "state.json"),
            Path.Combine(Path.GetFullPath(directory), "state.backup.json"),
            fileSystem ?? new PhysicalAtomicFileSystem(),
            fallbackWallpaper)
    {
    }

    public StateStore(
        string primaryPath,
        string backupPath,
        IAtomicFileSystem fileSystem,
        Func<WallpaperId?>? fallbackWallpaper = null)
    {
        _fallbackWallpaper = fallbackWallpaper ?? (static () => null);
        _store = new AtomicJsonStore<PersistedState>(
            primaryPath,
            backupPath,
            fileSystem,
            PersistenceJsonContext.Default.PersistedState,
            static () => PersistedState.Default,
            state => PersistedStateValidator.ValidateAndNormalize(state, _fallbackWallpaper()),
            [new StateV0ToV1Migration()]);
    }

    public Task<PersistenceLoadResult<PersistedState>> LoadAsync(CancellationToken cancellationToken) =>
        _store.LoadAsync(cancellationToken);

    public Task SaveAsync(PersistedState state, CancellationToken cancellationToken)
    {
        var normalized = PersistedStateValidator.ValidateAndNormalize(state, _fallbackWallpaper());
        return _store.SaveAsync(normalized, cancellationToken);
    }

    private static string DefaultDirectory() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "VibeWallpaper");
}
