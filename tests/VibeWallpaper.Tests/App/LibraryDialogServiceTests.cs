using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Tests.App;

public sealed class LibraryDialogServiceTests
{
    [Fact]
    public async Task ConfirmRemove_NamesWallpaperAndExplainsSourceIsPreserved()
    {
        var dialogs = new RecordingRemoveDialogPresenter { Result = true };
        var service = new LibraryDialogService(dialogs, new RecordingProcessLauncher());

        var confirmed = await service.ConfirmRemoveAsync(
            "Aurora",
            true,
            TestContext.Current.CancellationToken);

        Assert.True(confirmed);
        Assert.NotNull(dialogs.Request);
        Assert.Contains("Aurora", dialogs.Request.Content, StringComparison.Ordinal);
        Assert.Contains("Tệp hoặc thư mục nguồn sẽ không bị xóa hay thay đổi.", dialogs.Request.Content, StringComparison.Ordinal);
        Assert.Equal("Xóa khỏi thư viện", dialogs.Request.PrimaryButtonText);
        Assert.Equal("Hủy", dialogs.Request.CloseButtonText);
    }

    [Fact]
    public async Task OpenVideoLocation_UsesExplorerArgumentListWithoutShellConcatenation()
    {
        var launcher = new RecordingProcessLauncher();
        var service = new LibraryDialogService(new RecordingRemoveDialogPresenter(), launcher);
        var item = VideoItem(@"C:\wallpapers\aurora demo.mp4");

        var result = await service.OpenSourceLocationAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.NotNull(launcher.Request);
        Assert.Equal("explorer.exe", launcher.Request.FileName);
        Assert.False(launcher.Request.UseShellExecute);
        Assert.Equal(["/select,", Path.GetFullPath(@"C:\wallpapers\aurora demo.mp4")], launcher.Request.Arguments);
    }

    [Fact]
    public async Task OpenWebLocation_OpensCanonicalRootDirectory()
    {
        var launcher = new RecordingProcessLauncher();
        var service = new LibraryDialogService(new RecordingRemoveDialogPresenter(), launcher);
        var item = WebItem(@"C:\wallpapers\web scene");

        var result = await service.OpenSourceLocationAsync(
            item,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal([Path.TrimEndingDirectorySeparator(Path.GetFullPath(@"C:\wallpapers\web scene"))], launcher.Request?.Arguments);
    }

    [Fact]
    public async Task OpenSourceLocation_LaunchFailureReturnsTypedUserError()
    {
        var service = new LibraryDialogService(
            new RecordingRemoveDialogPresenter(),
            new RecordingProcessLauncher { Failure = new InvalidOperationException("injected") });

        var result = await service.OpenSourceLocationAsync(
            VideoItem(@"C:\wallpapers\aurora.mp4"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("library.source.open_failed", result.ErrorCode);
        Assert.Equal("Không thể mở vị trí tệp nguồn.", result.UserMessage);
    }

    private static WallpaperLibraryItem VideoItem(string path) => Item(
        "Video",
        VideoSource.Create(Path.GetFullPath(path)),
        new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(10), 30, "h264", false));

    private static WallpaperLibraryItem WebItem(string path) => Item(
        "Web",
        WebSource.Create(Path.GetFullPath(path), "index.html"),
        null);

    private static WallpaperLibraryItem Item(string name, WallpaperSource source, VideoMetadata? video)
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(), name, source, FitMode.Cover, 30, false, false, 0, false);
        return new WallpaperLibraryItem(
            definition,
            null,
            video,
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UnixEpoch));
    }

    private sealed class RecordingRemoveDialogPresenter : IRemoveDialogPresenter
    {
        public bool Result { get; init; }
        public RemoveDialogRequest? Request { get; private set; }

        public Task<bool> ShowAsync(RemoveDialogRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(Result);
        }
    }

    private sealed class RecordingProcessLauncher : IProcessLauncher
    {
        public ProcessLaunchRequest? Request { get; private set; }
        public Exception? Failure { get; init; }

        public void Launch(ProcessLaunchRequest request)
        {
            if (Failure is not null) throw Failure;
            Request = request;
        }
    }
}
