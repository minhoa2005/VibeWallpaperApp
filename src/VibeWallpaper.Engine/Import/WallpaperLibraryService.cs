using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import.Video;

namespace VibeWallpaper.Engine.Import;

public sealed class WallpaperImportException : Exception
{
    public WallpaperImportException(
        SourceValidationStatus status,
        string diagnosticCode,
        string message)
        : base(message)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentException("A defined status is required.", nameof(status));
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        Status = status;
        DiagnosticCode = diagnosticCode;
    }

    public WallpaperImportException(
        SourceValidationStatus status,
        string diagnosticCode,
        string message,
        Exception innerException)
        : base(message, innerException)
    {
        if (!Enum.IsDefined(status))
            throw new ArgumentException("A defined status is required.", nameof(status));
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        Status = status;
        DiagnosticCode = diagnosticCode;
    }

    public SourceValidationStatus Status { get; }
    public string DiagnosticCode { get; }
}

public sealed class WallpaperLibraryService
{
    private readonly IStateStore _stateStore;
    private readonly IWallpaperImportPreparer _preparer;
    private readonly SemaphoreSlim _stateGate = new(1, 1);

    public WallpaperLibraryService(
        IStateStore stateStore,
        IVideoProbeService probe,
        IVideoThumbnailService? thumbnailService = null,
        TimeProvider? timeProvider = null)
        : this(
            stateStore,
            new WallpaperImportPreparer(probe, thumbnailService, timeProvider))
    {
    }

    internal WallpaperLibraryService(
        IStateStore stateStore,
        IWallpaperImportPreparer preparer)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(preparer);
        _stateStore = stateStore;
        _preparer = preparer;
    }

    public async Task<WallpaperLibraryItem> ImportVideoAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var item = await _preparer.PrepareVideoAsync(
            sourcePath, cancellationToken).ConfigureAwait(false);
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await _stateStore.LoadAsync(
                cancellationToken).ConfigureAwait(false)).Value;
            var next = new PersistedState(
                current.SchemaVersion,
                [.. current.Library, item],
                current.Assignments,
                current.Groups,
                current.AudioOwner);
            await _stateStore.SaveAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _stateGate.Release();
        }

        return item;
    }

    public async Task<SourceValidation> RevalidateAsync(
        WallpaperId wallpaperId,
        CancellationToken cancellationToken)
    {
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var current = (await _stateStore.LoadAsync(
                cancellationToken).ConfigureAwait(false)).Value;
            var index = FindItemIndex(current.Library, wallpaperId);
            var item = current.Library[index];
            var updated = await _preparer.RevalidateAsync(
                item, cancellationToken).ConfigureAwait(false);
            if (Equals(updated, item)) return item.Validation;

            var library = current.Library.ToArray();
            library[index] = updated;
            await _stateStore.SaveAsync(
                new PersistedState(
                    current.SchemaVersion,
                    library,
                    current.Assignments,
                    current.Groups,
                    current.AudioOwner),
                cancellationToken).ConfigureAwait(false);
            return updated.Validation;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    private static int FindItemIndex(
        IReadOnlyList<WallpaperLibraryItem> library,
        WallpaperId id)
    {
        for (var index = 0; index < library.Count; index++)
        {
            if (library[index].Definition.Id == id) return index;
        }

        throw new KeyNotFoundException(
            $"Wallpaper '{id.Value}' was not found in the library.");
    }
}
