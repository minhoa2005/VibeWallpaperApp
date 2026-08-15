using Windows.Storage.Pickers;

namespace VibeWallpaper.App.Services;

public sealed class ContentPicker : IContentPicker
{
    private static readonly IReadOnlyList<string> VideoExtensions =
        [".mp4", ".webm", ".mkv", ".mov", ".gif"];

    private readonly nint _ownerHwnd;
    private readonly IStoragePickerAdapter _adapter;

    public ContentPicker(nint ownerHwnd)
        : this(ownerHwnd, new WinUiStoragePickerAdapter())
    {
    }

    internal ContentPicker(nint ownerHwnd, IStoragePickerAdapter adapter)
    {
        if (ownerHwnd == 0) throw new ArgumentException("A management window handle is required.", nameof(ownerHwnd));
        ArgumentNullException.ThrowIfNull(adapter);
        _ownerHwnd = ownerHwnd;
        _adapter = adapter;
    }

    public async Task<IReadOnlyList<string>> PickVideoFilesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var paths = await _adapter.PickFilesAsync(
            _ownerHwnd,
            VideoExtensions,
            allowMultiple: true).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return paths?.ToArray() ?? [];
    }

    public async Task<string?> PickWebDirectoryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = await _adapter.PickFolderAsync(_ownerHwnd).ConfigureAwait(true);
        cancellationToken.ThrowIfCancellationRequested();
        return path;
    }

    private sealed class WinUiStoragePickerAdapter : IStoragePickerAdapter
    {
        public async Task<IReadOnlyList<string>?> PickFilesAsync(
            nint ownerHwnd,
            IReadOnlyList<string> extensions,
            bool allowMultiple)
        {
            var picker = new FileOpenPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
            foreach (var extension in extensions) picker.FileTypeFilter.Add(extension);

            if (allowMultiple)
            {
                var files = await picker.PickMultipleFilesAsync();
                return files?.Select(static file => file.Path).ToArray();
            }

            var file = await picker.PickSingleFileAsync();
            return file is null ? null : [file.Path];
        }

        public async Task<string?> PickFolderAsync(nint ownerHwnd)
        {
            var picker = new FolderPicker();
            WinRT.Interop.InitializeWithWindow.Initialize(picker, ownerHwnd);
            picker.FileTypeFilter.Add("*");
            var folder = await picker.PickSingleFolderAsync();
            return folder?.Path;
        }
    }
}
