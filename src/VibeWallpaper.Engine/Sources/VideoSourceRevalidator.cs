using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Engine.Sources;

public sealed class VideoSourceRevalidator
{
    private readonly IStateStore _stateStore;
    private readonly WallpaperLibraryService _library;
    private readonly FallbackRendererCoordinator _fallback;
    private readonly Func<WallpaperKind, bool> _rendererAvailable;

    public VideoSourceRevalidator(
        IStateStore stateStore,
        WallpaperLibraryService library,
        FallbackRendererCoordinator fallback,
        Func<WallpaperKind, bool> rendererAvailable)
    {
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(fallback);
        ArgumentNullException.ThrowIfNull(rendererAvailable);
        _stateStore = stateStore;
        _library = library;
        _fallback = fallback;
        _rendererAvailable = rendererAvailable;
    }

    public async Task<SourceValidation> RevalidateBeforeActivationAsync(
        WallpaperId wallpaperId,
        CancellationToken cancellationToken)
    {
        var validation = await _library.RevalidateAsync(wallpaperId, cancellationToken).ConfigureAwait(false);
        var state = (await _stateStore.LoadAsync(cancellationToken).ConfigureAwait(false)).Value;
        _fallback.UpdatePersistedState(state);
        var item = state.Library.FirstOrDefault(candidate => candidate.Definition.Id == wallpaperId)
            ?? throw new KeyNotFoundException($"Wallpaper '{wallpaperId.Value}' was not found in the library.");
        foreach (var assignment in state.Assignments.Where(candidate => candidate.Wallpaper == wallpaperId))
        {
            await _fallback.ReconcileAsync(
                assignment.Monitor.Identity,
                validation.Status,
                _rendererAvailable(item.Definition.Source.Kind),
                cancellationToken).ConfigureAwait(false);
        }

        return validation;
    }
}
