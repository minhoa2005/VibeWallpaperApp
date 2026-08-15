namespace VibeWallpaper.App.Services;

public interface IContentPicker
{
    Task<IReadOnlyList<string>> PickVideoFilesAsync(CancellationToken cancellationToken);

    Task<string?> PickWebDirectoryAsync(CancellationToken cancellationToken);
}

internal interface IStoragePickerAdapter
{
    Task<IReadOnlyList<string>?> PickFilesAsync(
        nint ownerHwnd,
        IReadOnlyList<string> extensions,
        bool allowMultiple);

    Task<string?> PickFolderAsync(nint ownerHwnd);
}
