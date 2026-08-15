using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Diagnostics;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.App;

public sealed class LibraryControllerTests
{
    [Fact]
    public async Task ImportVideo_SuccessCommitsPreparedItemAndReturnsIt()
    {
        var item = Item("Aurora");
        var authority = new RecordingAuthority();
        var controller = new LibraryController(
            new RecordingPreparer { VideoResult = item },
            authority);

        var result = await controller.ImportVideoAsync(
            Path.GetFullPath("aurora.mp4"),
            TestContext.Current.CancellationToken);

        Assert.True(result.Result.Succeeded);
        Assert.Equal(item.Definition.Id, result.ImportedItem?.Definition.Id);
        Assert.Equal(item.Definition.Id, Assert.Single(authority.Items).Definition.Id);
    }

    [Fact]
    public async Task ImportVideo_TypedFailurePreservesCodeAndCreatesNoItem()
    {
        var authority = new RecordingAuthority();
        var controller = new LibraryController(
            new RecordingPreparer
            {
                Failure = new WallpaperImportException(
                    SourceValidationStatus.Unsupported,
                    "video.source.unsupported",
                    "unsupported"),
            },
            authority);

        var result = await controller.ImportVideoAsync(
            Path.GetFullPath("wallpaper.txt"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Result.Succeeded);
        Assert.Equal("video.source.unsupported", result.Result.ErrorCode);
        Assert.Equal("Định dạng video chưa được hỗ trợ.", result.Result.UserMessage);
        Assert.Null(result.ImportedItem);
        Assert.Empty(authority.Items);
    }

    [Fact]
    public async Task ImportVideo_CallerCancellationPropagatesWithoutCommit()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var authority = new RecordingAuthority();
        var controller = new LibraryController(
            new RecordingPreparer { ObserveCancellation = true },
            authority);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            controller.ImportVideoAsync(Path.GetFullPath("aurora.mp4"), cancellation.Token));

        Assert.Empty(authority.Items);
    }

    [Fact]
    public async Task ImportVideo_DuplicateAuthorityFailureReturnsTypedMessageAndNoItem()
    {
        var item = Item("Aurora");
        var authority = new RecordingAuthority
        {
            AddFailure = new LibraryStateException("library.item.duplicate", "duplicate"),
        };
        var controller = new LibraryController(
            new RecordingPreparer { VideoResult = item },
            authority);

        var result = await controller.ImportVideoAsync(
            Path.GetFullPath("aurora.mp4"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Result.Succeeded);
        Assert.Equal("library.item.duplicate", result.Result.ErrorCode);
        Assert.Equal("Wallpaper này đã có trong thư viện.", result.Result.UserMessage);
        Assert.Null(result.ImportedItem);
        Assert.Empty(authority.Items);
    }

    [Fact]
    public async Task ImportVideo_UnexpectedFailureReturnsGenericCodeAndLogs()
    {
        var log = new RecordingLog();
        var controller = new LibraryController(
            new RecordingPreparer { Failure = new IOException("injected") },
            new RecordingAuthority(),
            log);

        var result = await controller.ImportVideoAsync(
            Path.GetFullPath("aurora.mp4"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Result.Succeeded);
        Assert.Equal("library.operation.failed", result.Result.ErrorCode);
        Assert.Single(log.Messages);
    }

    [Fact]
    public async Task ImportVideo_MediaRuntimeUnavailableReturnsActionableTypedFailure()
    {
        var controller = new LibraryController(
            new RecordingPreparer
            {
                Failure = new VibeWallpaper.Engine.Import.Video.LibVlcRuntimeUnavailableException(
                    "VibeWallpaper.MediaProbe.dll is missing."),
            },
            new RecordingAuthority());

        var result = await controller.ImportVideoAsync(
            Path.GetFullPath("aurora.mp4"),
            TestContext.Current.CancellationToken);

        Assert.False(result.Result.Succeeded);
        Assert.Equal("video.runtime.unavailable", result.Result.ErrorCode);
        Assert.Contains("thành phần xử lý video", result.Result.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Revalidate_SuccessReplacesExistingItem()
    {
        var original = Item("Before");
        var updated = Item("After", original.Definition.Id);
        var authority = new RecordingAuthority([original]);
        var controller = new LibraryController(
            new RecordingPreparer { RevalidationResult = updated },
            authority);

        var result = await controller.RevalidateAsync(
            original.Definition.Id,
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal("After", Assert.Single(authority.Items).Definition.Name);
    }

    [Fact]
    public async Task SetNetworkPermission_NonWebReturnsTypedFailureWithoutMutation()
    {
        var item = Item("Video");
        var authority = new RecordingAuthority([item]);
        var controller = new LibraryController(new RecordingPreparer(), authority);

        var result = await controller.SetNetworkPermissionAsync(
            item.Definition.Id,
            true,
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal("library.network.web_required", result.ErrorCode);
        Assert.Equal(0, authority.PermissionChanges);
    }

    private static WallpaperLibraryItem Item(string name, WallpaperId? id = null)
    {
        var definition = new WallpaperDefinition(
            id ?? WallpaperId.New(),
            name,
            VideoSource.Create(Path.GetFullPath($"{name}.mp4")),
            FitMode.Cover,
            30,
            false,
            false,
            0,
            false);
        return new WallpaperLibraryItem(
            definition,
            null,
            new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(10), 30, "h264", false),
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UnixEpoch));
    }

    private sealed class RecordingPreparer : IWallpaperImportPreparer
    {
        public WallpaperLibraryItem? VideoResult { get; init; }
        public WallpaperLibraryItem? WebResult { get; init; }
        public WallpaperLibraryItem? RevalidationResult { get; init; }
        public Exception? Failure { get; init; }
        public bool ObserveCancellation { get; init; }

        public Task<WallpaperLibraryItem> PrepareVideoAsync(string sourcePath, CancellationToken cancellationToken)
        {
            if (ObserveCancellation) cancellationToken.ThrowIfCancellationRequested();
            return Result(VideoResult);
        }

        public Task<WallpaperLibraryItem> PrepareWebAsync(string sourceDirectory, CancellationToken cancellationToken) =>
            Result(WebResult);

        public Task<WallpaperLibraryItem> RevalidateAsync(
            WallpaperLibraryItem item,
            CancellationToken cancellationToken) =>
            Result(RevalidationResult);

        private Task<WallpaperLibraryItem> Result(WallpaperLibraryItem? item) =>
            Failure is not null
                ? Task.FromException<WallpaperLibraryItem>(Failure)
                : Task.FromResult(item ?? throw new InvalidOperationException("No result configured."));
    }

    private sealed class RecordingAuthority : ILibraryStateAuthority
    {
        private readonly List<WallpaperLibraryItem> _items;
        private long _version;

        public RecordingAuthority(IEnumerable<WallpaperLibraryItem>? items = null) =>
            _items = items?.ToList() ?? [];

        public IReadOnlyList<WallpaperLibraryItem> Items => _items;
        public int PermissionChanges { get; private set; }
        public Exception? AddFailure { get; init; }

        public LibraryStateSnapshot GetLibrarySnapshot() => new(_version, _items.ToArray());

        public Task<LibraryStateSnapshot> AddLibraryItemAsync(
            WallpaperLibraryItem item,
            CancellationToken cancellationToken)
        {
            if (AddFailure is not null) return Task.FromException<LibraryStateSnapshot>(AddFailure);
            _items.Add(item);
            return Task.FromResult(Snapshot());
        }

        public Task<LibraryStateSnapshot> ReplaceLibraryItemAsync(
            WallpaperLibraryItem item,
            CancellationToken cancellationToken)
        {
            var index = _items.FindIndex(existing => existing.Definition.Id == item.Definition.Id);
            if (index < 0) throw new LibraryStateException("library.item.missing", "missing");
            _items[index] = item;
            return Task.FromResult(Snapshot());
        }

        public Task<LibraryStateSnapshot> RemoveLibraryItemAsync(
            WallpaperId id,
            bool clearAssignments,
            CancellationToken cancellationToken)
        {
            _items.RemoveAll(item => item.Definition.Id == id);
            return Task.FromResult(Snapshot());
        }

        public Task<LibraryStateSnapshot> SetWebNetworkPermissionAsync(
            WallpaperId id,
            bool enabled,
            CancellationToken cancellationToken)
        {
            PermissionChanges++;
            return Task.FromResult(Snapshot());
        }

        private LibraryStateSnapshot Snapshot() => new(++_version, _items.ToArray());
    }

    private sealed class RecordingLog : ILogSink
    {
        public List<string> Messages { get; } = [];

        public ValueTask WriteAsync(
            string level,
            string message,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Messages.Add(message);
            return ValueTask.CompletedTask;
        }

        public ValueTask WriteEventAsync(
            DiagnosticEvent diagnosticEvent,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
