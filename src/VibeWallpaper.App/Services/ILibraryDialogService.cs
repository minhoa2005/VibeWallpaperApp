using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.App.Services;

public interface ILibraryDialogService
{
    Task<bool> ConfirmRemoveAsync(
        string wallpaperName,
        bool isAssigned,
        CancellationToken cancellationToken);

    Task<CommandResult> OpenSourceLocationAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken);
}

internal sealed record RemoveDialogRequest(
    string Title,
    string Content,
    string PrimaryButtonText,
    string CloseButtonText,
    bool IsAssigned);

internal interface IRemoveDialogPresenter
{
    Task<bool> ShowAsync(RemoveDialogRequest request, CancellationToken cancellationToken);
}

internal sealed record ProcessLaunchRequest(
    string FileName,
    bool UseShellExecute,
    IReadOnlyList<string> Arguments);

internal interface IProcessLauncher
{
    void Launch(ProcessLaunchRequest request);
}
