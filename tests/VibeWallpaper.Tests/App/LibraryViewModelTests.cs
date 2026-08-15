using VibeWallpaper.App.Services;
using VibeWallpaper.App.ViewModels;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Tests.App;

public sealed class LibraryViewModelTests
{
    [Fact]
    public void Filter_CombinesUnicodeNameKindAndStatusWithStableNameOrder()
    {
        var vm = CreateViewModel([
            WebItem("Bầu trời", SourceValidationStatus.Available),
            VideoItem("bầu biển", SourceValidationStatus.Missing),
            VideoItem("Ocean", SourceValidationStatus.Available)]);

        vm.SearchText = "BẦU";
        vm.SelectedKindFilter = vm.KindFilters.Single(option => option.Value == WallpaperKind.Video);
        vm.SelectedStatusFilter = vm.StatusFilters.Single(option => option.Value == SourceValidationStatus.Missing);

        Assert.Equal("bầu biển", Assert.Single(vm.FilteredItems).Name);
    }

    [Theory]
    [InlineData(SourceValidationStatus.Available)]
    [InlineData(SourceValidationStatus.Changed)]
    [InlineData(SourceValidationStatus.Missing)]
    [InlineData(SourceValidationStatus.Invalid)]
    [InlineData(SourceValidationStatus.Unsupported)]
    public void ItemProjection_AllValidationStatusesHaveTextLabels(SourceValidationStatus status)
    {
        var item = Assert.Single(CreateViewModel([VideoItem("Status", status)]).Items);

        Assert.False(string.IsNullOrWhiteSpace(item.StatusLabel));
        Assert.Contains(status.ToString(), item.AutomationStatusText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportVideos_PartialFailureKeepsSuccessfulItemsAndShowsErrorSummary()
    {
        var good = VideoItem("Good", SourceValidationStatus.Available);
        var controller = new RecordingController
        {
            VideoResults = new Queue<ImportResult>([
                SuccessImport(good),
                FailedImport("video.source.unsupported", "Định dạng video chưa được hỗ trợ.")]),
        };
        var picker = new RecordingPicker { VideoPaths = [@"C:\good.mp4", @"C:\bad.txt"] };
        var vm = CreateViewModel([], controller, picker);

        await vm.ImportVideosAsync(TestContext.Current.CancellationToken);

        Assert.Contains(vm.Items, item => item.Name == "Good");
        Assert.True(vm.Notice.IsOpen);
        Assert.Equal(UserNoticeSeverity.Error, vm.Notice.Severity);
        Assert.Contains("1", vm.Notice.Message, StringComparison.Ordinal);
        Assert.Contains(@"C:\bad.txt", vm.Notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("video.source.unsupported", vm.Notice.Message, StringComparison.Ordinal);
        Assert.Contains("Nguyên nhân", vm.Notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cách xử lý", vm.Notice.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ImportWeb_UserClosesPicker_IsSilentAndRestoresBusy()
    {
        var controller = new RecordingController();
        var vm = CreateViewModel([], controller, new RecordingPicker());

        await vm.ImportWebAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, controller.WebImports);
        Assert.False(vm.Notice.IsOpen);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task ImportVideos_UnexpectedPickerFailureShowsGenericNoticeAndRestoresBusy()
    {
        var vm = CreateViewModel(
            [],
            new RecordingController(),
            new RecordingPicker { Failure = new IOException("injected") });

        await vm.ImportVideosAsync(TestContext.Current.CancellationToken);

        Assert.False(vm.IsBusy);
        Assert.True(vm.Notice.IsOpen);
        Assert.Equal("library.operation.failed", vm.Notice.ErrorCode);
    }

    [Fact]
    public void UseWallpaper_RaisesOnlyForAvailableItems()
    {
        var available = VideoItem("Good", SourceValidationStatus.Available);
        var missing = VideoItem("Missing", SourceValidationStatus.Missing);
        var vm = CreateViewModel([available, missing]);
        var requested = new List<WallpaperId>();
        vm.UseWallpaperRequested += requested.Add;

        vm.UseWallpaper(vm.Items.Single(item => item.Name == "Missing"));
        vm.UseWallpaper(vm.Items.Single(item => item.Name == "Good"));

        Assert.Equal([available.Definition.Id], requested);
    }

    [Fact]
    public async Task Remove_DeclinedConfirmationDoesNotCallControllerOrShowNotice()
    {
        var item = VideoItem("Keep", SourceValidationStatus.Available);
        var controller = new RecordingController([item]);
        var dialogs = new RecordingDialogs { ConfirmResult = false };
        var vm = CreateViewModel([item], controller, dialogs: dialogs);

        await vm.RemoveAsync(Assert.Single(vm.Items), TestContext.Current.CancellationToken);

        Assert.Equal(0, controller.Removes);
        Assert.False(vm.Notice.IsOpen);
        Assert.Single(vm.Items);
    }

    [Fact]
    public async Task Remove_AssignedItemConfirmsAndClearsAssignments()
    {
        var item = VideoItem("Assigned", SourceValidationStatus.Available);
        var controller = new RecordingController([item]);
        var dialogs = new RecordingDialogs { ConfirmResult = true };
        var vm = CreateViewModel(
            [item],
            controller,
            dialogs: dialogs,
            assignedIds: new HashSet<WallpaperId> { item.Definition.Id });

        await vm.RemoveAsync(Assert.Single(vm.Items), TestContext.Current.CancellationToken);

        Assert.True(dialogs.LastIsAssigned);
        Assert.True(controller.LastClearAssignments);
        Assert.Empty(vm.Items);
    }

    [Fact]
    public async Task SetNetworkPermission_FailureLeavesProjectionAtPublishedValue()
    {
        var item = WebItem("Local", SourceValidationStatus.Available, networkEnabled: false);
        var controller = new RecordingController([item])
        {
            NetworkResult = new CommandResult(false, "library.operation.failed", "Không thể hoàn tất."),
        };
        var vm = CreateViewModel([item], controller);

        await vm.SetNetworkPermissionAsync(
            Assert.Single(vm.Items),
            true,
            TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(vm.Items).NetworkEnabled);
        Assert.True(vm.Notice.IsOpen);
        Assert.False(vm.IsBusy);
    }

    private static LibraryViewModel CreateViewModel(
        IReadOnlyList<WallpaperLibraryItem> items,
        RecordingController? controller = null,
        RecordingPicker? picker = null,
        RecordingDialogs? dialogs = null,
        IReadOnlySet<WallpaperId>? assignedIds = null)
    {
        controller ??= new RecordingController(items);
        return new LibraryViewModel(
            controller,
            picker ?? new RecordingPicker(),
            dialogs ?? new RecordingDialogs(),
            new LibrarySnapshot(0, items, assignedIds ?? new HashSet<WallpaperId>()));
    }

    private static ImportResult SuccessImport(WallpaperLibraryItem item) =>
        new(new CommandResult(true, null, "Đã thêm."), item);

    private static ImportResult FailedImport(string code, string message) =>
        new(new CommandResult(false, code, message), null);

    private static WallpaperLibraryItem VideoItem(string name, SourceValidationStatus status) =>
        Item(name, VideoSource.Create(Path.GetFullPath($@"C:\wallpapers\{name}.mp4")), status,
            new VideoMetadata(1920, 1080, TimeSpan.FromSeconds(10), 30, "h264", false), false);

    private static WallpaperLibraryItem WebItem(
        string name,
        SourceValidationStatus status,
        bool networkEnabled = false) =>
        Item(name, WebSource.Create(Path.GetFullPath($@"C:\wallpapers\{name}"), "index.html"), status, null, networkEnabled);

    private static WallpaperLibraryItem Item(
        string name,
        WallpaperSource source,
        SourceValidationStatus status,
        VideoMetadata? video,
        bool networkEnabled)
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(), name, source, FitMode.Cover, 30, networkEnabled, false, 0, false);
        return new WallpaperLibraryItem(
            definition, null, video,
            new SourceValidation(status, null, null, DateTimeOffset.UnixEpoch));
    }

    private sealed class RecordingPicker : IContentPicker
    {
        public IReadOnlyList<string> VideoPaths { get; init; } = [];
        public string? WebPath { get; init; }
        public Exception? Failure { get; init; }

        public Task<IReadOnlyList<string>> PickVideoFilesAsync(CancellationToken cancellationToken) =>
            Failure is null
                ? Task.FromResult(VideoPaths)
                : Task.FromException<IReadOnlyList<string>>(Failure);

        public Task<string?> PickWebDirectoryAsync(CancellationToken cancellationToken) =>
            Failure is null
                ? Task.FromResult(WebPath)
                : Task.FromException<string?>(Failure);
    }

    private sealed class RecordingDialogs : ILibraryDialogService
    {
        public bool ConfirmResult { get; init; }
        public bool LastIsAssigned { get; private set; }

        public Task<bool> ConfirmRemoveAsync(string wallpaperName, bool isAssigned, CancellationToken cancellationToken)
        {
            LastIsAssigned = isAssigned;
            return Task.FromResult(ConfirmResult);
        }

        public Task<CommandResult> OpenSourceLocationAsync(WallpaperLibraryItem item, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(true, null, "Đã mở."));
    }

    private sealed class RecordingController : ILibraryController
    {
        private readonly List<WallpaperLibraryItem> _items;
        private long _version;

        public RecordingController(IEnumerable<WallpaperLibraryItem>? items = null) =>
            _items = items?.ToList() ?? [];

        public Queue<ImportResult> VideoResults { get; init; } = new();
        public CommandResult NetworkResult { get; init; } = new(true, null, "Đã cập nhật.");
        public int WebImports { get; private set; }
        public int Removes { get; private set; }
        public bool LastClearAssignments { get; private set; }

        public Task<LibrarySnapshot> GetLibraryAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new LibrarySnapshot(_version, _items.ToArray(), new HashSet<WallpaperId>()));

        public Task<ImportResult> ImportVideoAsync(string absolutePath, CancellationToken cancellationToken)
        {
            var result = VideoResults.Dequeue();
            if (result.Result.Succeeded && result.ImportedItem is not null)
            {
                _items.Add(result.ImportedItem);
                _version++;
            }
            return Task.FromResult(result);
        }

        public Task<ImportResult> ImportWebAsync(string absoluteDirectory, CancellationToken cancellationToken)
        {
            WebImports++;
            throw new InvalidOperationException("No web result configured.");
        }

        public Task<CommandResult> RevalidateAsync(WallpaperId id, CancellationToken cancellationToken) =>
            Task.FromResult(new CommandResult(true, null, "Đã kiểm tra."));

        public Task<CommandResult> RemoveAsync(WallpaperId id, bool clearAssignments, CancellationToken cancellationToken)
        {
            Removes++;
            LastClearAssignments = clearAssignments;
            _items.RemoveAll(item => item.Definition.Id == id);
            _version++;
            return Task.FromResult(new CommandResult(true, null, "Đã xóa."));
        }

        public Task<CommandResult> SetNetworkPermissionAsync(WallpaperId id, bool enabled, CancellationToken cancellationToken) =>
            Task.FromResult(NetworkResult);
    }
}
