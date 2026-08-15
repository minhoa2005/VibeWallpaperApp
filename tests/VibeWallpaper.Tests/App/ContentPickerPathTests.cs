using VibeWallpaper.App.Services;

namespace VibeWallpaper.Tests.App;

public sealed class ContentPickerPathTests
{
    [Fact]
    public async Task PickVideoFiles_UsesOwnerHwndMultiSelectAndExactMvpExtensions()
    {
        var adapter = new RecordingStoragePickerAdapter
        {
            FileResult = [@"C:\wallpapers\one.mp4", @"C:\wallpapers\two.webm"],
        };
        var picker = new ContentPicker((nint)42, adapter);

        var result = await picker.PickVideoFilesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(adapter.FileResult, result);
        Assert.Equal((nint)42, adapter.FileOwner);
        Assert.True(adapter.AllowMultiple);
        Assert.Equal([".mp4", ".webm", ".mkv", ".mov", ".gif"], adapter.Extensions);
    }

    [Fact]
    public async Task PickVideoFiles_UserClosesPicker_ReturnsEmptyCollection()
    {
        var picker = new ContentPicker((nint)42, new RecordingStoragePickerAdapter());

        var result = await picker.PickVideoFilesAsync(TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task PickWebDirectory_UsesOwnerHwndAndReturnsNullWhenCancelled()
    {
        var adapter = new RecordingStoragePickerAdapter();
        var picker = new ContentPicker((nint)84, adapter);

        var result = await picker.PickWebDirectoryAsync(TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal((nint)84, adapter.FolderOwner);
    }

    [Fact]
    public async Task Picker_CallerCancellationAfterFrameworkAwait_Propagates()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new RecordingStoragePickerAdapter
        {
            FileResult = [@"C:\wallpapers\one.mp4"],
            OnFilesPicked = cancellation.Cancel,
        };
        var picker = new ContentPicker((nint)42, adapter);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            picker.PickVideoFilesAsync(cancellation.Token));
    }

    private sealed class RecordingStoragePickerAdapter : IStoragePickerAdapter
    {
        public IReadOnlyList<string>? FileResult { get; init; }
        public Action? OnFilesPicked { get; init; }
        public nint FileOwner { get; private set; }
        public nint FolderOwner { get; private set; }
        public bool AllowMultiple { get; private set; }
        public IReadOnlyList<string> Extensions { get; private set; } = [];

        public Task<IReadOnlyList<string>?> PickFilesAsync(
            nint ownerHwnd,
            IReadOnlyList<string> extensions,
            bool allowMultiple)
        {
            FileOwner = ownerHwnd;
            Extensions = extensions.ToArray();
            AllowMultiple = allowMultiple;
            OnFilesPicked?.Invoke();
            return Task.FromResult(FileResult);
        }

        public Task<string?> PickFolderAsync(nint ownerHwnd)
        {
            FolderOwner = ownerHwnd;
            return Task.FromResult<string?>(null);
        }
    }
}
