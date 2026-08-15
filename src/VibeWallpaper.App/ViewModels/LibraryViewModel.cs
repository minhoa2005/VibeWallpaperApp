using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.App.ViewModels;

public sealed record LibraryKindFilter(WallpaperKind? Value, string Label);

public sealed record LibraryStatusFilter(SourceValidationStatus? Value, string Label);

public sealed class LibraryItemViewModel
{
    public LibraryItemViewModel(WallpaperLibraryItem item, bool isAssigned = false)
    {
        Item = item ?? throw new ArgumentNullException(nameof(item));
        IsAssigned = isAssigned;
    }

    public WallpaperLibraryItem Item { get; }
    public WallpaperId Id => Item.Definition.Id;
    public string Name => Item.Definition.Name;
    public WallpaperKind Kind => Item.Definition.Source.Kind;
    public string KindLabel => Kind switch
    {
        WallpaperKind.Video => "Video",
        WallpaperKind.Web => "Web",
        _ => "Màu nền",
    };
    public string SourcePath => Item.Definition.Source switch
    {
        VideoSource video => video.FilePath,
        WebSource web => web.DirectoryPath,
        SolidColorSource color => color.HexColor,
        _ => string.Empty,
    };
    public SourceValidation Validation => Item.Validation;
    public SourceValidationStatus Status => Validation.Status;
    public string StatusLabel => Status switch
    {
        SourceValidationStatus.Available => "Sẵn sàng",
        SourceValidationStatus.Changed => "Nguồn đã thay đổi",
        SourceValidationStatus.Missing => "Không tìm thấy nguồn",
        SourceValidationStatus.Invalid => "Nguồn không hợp lệ",
        SourceValidationStatus.Unsupported => "Định dạng chưa hỗ trợ",
        _ => "Không xác định",
    };
    public string AutomationStatusText => $"{StatusLabel} ({Status})";
    public string MetadataSummary => Item.Video is { } video
        ? $"{video.Width} × {video.Height} · {FormatDuration(video.Duration)}" +
          (video.NominalFps is { } fps ? $" · {fps:0.#} FPS" : string.Empty)
        : Kind == WallpaperKind.Web ? "Thư mục web cục bộ" : string.Empty;
    public bool IsWeb => Kind == WallpaperKind.Web;
    public bool NetworkEnabled => Item.Definition.NetworkEnabled;
    public bool IsAssigned { get; }
    public bool CanUse => Status == SourceValidationStatus.Available;
    public bool CanRevalidate => Kind is WallpaperKind.Video or WallpaperKind.Web;
    public string UseAutomationId => $"UseWallpaper-{Id.Value:N}";
    public string RevalidateAutomationId => $"RevalidateWallpaper-{Id.Value:N}";
    public string OpenLocationAutomationId => $"OpenWallpaperLocation-{Id.Value:N}";
    public string RemoveAutomationId => $"RemoveWallpaper-{Id.Value:N}";
    public string NetworkAutomationId => $"WallpaperNetwork-{Id.Value:N}";

    private static string FormatDuration(TimeSpan duration) =>
        duration.TotalHours >= 1
            ? duration.ToString(@"h\:mm\:ss")
            : duration.ToString(@"m\:ss");
}

public sealed class LibraryViewModel : INotifyPropertyChanged
{
    private readonly ILibraryController _controller;
    private readonly IContentPicker _picker;
    private readonly ILibraryDialogService _dialogs;
    private string _searchText = string.Empty;
    private LibraryKindFilter _selectedKindFilter;
    private LibraryStatusFilter _selectedStatusFilter;
    private bool _isBusy;
    private UserNotice _notice = UserNotice.Closed;
    private IReadOnlySet<WallpaperId> _assignedIds = new HashSet<WallpaperId>();

    public LibraryViewModel(
        ILibraryController controller,
        IContentPicker picker,
        ILibraryDialogService dialogs,
        LibrarySnapshot initialSnapshot)
    {
        ArgumentNullException.ThrowIfNull(controller);
        ArgumentNullException.ThrowIfNull(picker);
        ArgumentNullException.ThrowIfNull(dialogs);
        ArgumentNullException.ThrowIfNull(initialSnapshot);
        _controller = controller;
        _picker = picker;
        _dialogs = dialogs;
        _selectedKindFilter = KindFilters[0];
        _selectedStatusFilter = StatusFilters[0];
        Replace(initialSnapshot);
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<WallpaperId>? UseWallpaperRequested;

    public ObservableCollection<LibraryItemViewModel> Items { get; } = [];

    public IReadOnlyList<LibraryKindFilter> KindFilters { get; } =
    [
        new(null, "Tất cả loại"),
        new(WallpaperKind.Video, "Video"),
        new(WallpaperKind.Web, "Web"),
    ];

    public IReadOnlyList<LibraryStatusFilter> StatusFilters { get; } =
    [
        new(null, "Tất cả trạng thái"),
        new(SourceValidationStatus.Available, "Sẵn sàng"),
        new(SourceValidationStatus.Changed, "Nguồn đã thay đổi"),
        new(SourceValidationStatus.Missing, "Không tìm thấy nguồn"),
        new(SourceValidationStatus.Invalid, "Nguồn không hợp lệ"),
        new(SourceValidationStatus.Unsupported, "Định dạng chưa hỗ trợ"),
    ];

    public string SearchText
    {
        get => _searchText;
        set
        {
            var next = value ?? string.Empty;
            if (string.Equals(_searchText, next, StringComparison.Ordinal)) return;
            _searchText = next;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredItems));
        }
    }

    public LibraryKindFilter SelectedKindFilter
    {
        get => _selectedKindFilter;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Equals(_selectedKindFilter, value)) return;
            _selectedKindFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredItems));
        }
    }

    public LibraryStatusFilter SelectedStatusFilter
    {
        get => _selectedStatusFilter;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (Equals(_selectedStatusFilter, value)) return;
            _selectedStatusFilter = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(FilteredItems));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public UserNotice Notice
    {
        get => _notice;
        private set
        {
            if (Equals(_notice, value)) return;
            _notice = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsNoticeOpen));
            OnPropertyChanged(nameof(NoticeTitle));
            OnPropertyChanged(nameof(NoticeMessage));
        }
    }

    public bool IsNoticeOpen => Notice.IsOpen;
    public string NoticeTitle => Notice.IsOpen
        ? Notice.Severity switch
        {
            UserNoticeSeverity.Success => "Hoàn tất",
            UserNoticeSeverity.Warning => "Cần chú ý",
            UserNoticeSeverity.Informational => "Thông tin",
            _ => UserErrorPresenter.Create(Notice.ErrorCode, Notice.Message).Title,
        }
        : string.Empty;
    public string NoticeMessage => Notice.Message;

    public IReadOnlyList<LibraryItemViewModel> FilteredItems => Items
        .Where(item => string.IsNullOrWhiteSpace(SearchText)
            || item.Name.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase))
        .Where(item => !SelectedKindFilter.Value.HasValue || item.Kind == SelectedKindFilter.Value.Value)
        .Where(item => !SelectedStatusFilter.Value.HasValue || item.Status == SelectedStatusFilter.Value.Value)
        .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
        .ThenBy(item => item.Id.Value)
        .ToArray();

    public Task ImportVideosAsync(CancellationToken cancellationToken) => RunBusyAsync(async () =>
    {
        var paths = await _picker.PickVideoFilesAsync(cancellationToken);
        if (paths.Count == 0) return;

        var successCount = 0;
        var failureCount = 0;
        (string Path, CommandResult Result)? firstFailure = null;
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await _controller.ImportVideoAsync(path, cancellationToken);
            if (result.Result.Succeeded)
            {
                successCount++;
            }
            else
            {
                failureCount++;
                firstFailure ??= (path, result.Result);
            }
        }

        await RefreshAsync(cancellationToken);
        if (firstFailure is { } failure)
        {
            var presentation = UserErrorPresenter.Create(failure.Result.ErrorCode, failure.Result.UserMessage);
            Notice = OpenNotice(
                UserNoticeSeverity.Error,
                $"Đã thêm {successCount} wallpaper; {failureCount} tệp không thể thêm.\n" +
                $"Tệp lỗi đầu tiên: {failure.Path}\n{presentation.DetailedMessage}",
                presentation.DiagnosticCode);
        }
        else
        {
            Notice = OpenNotice(UserNoticeSeverity.Success, $"Đã thêm {successCount} wallpaper.", null);
        }
    }, cancellationToken);

    public Task ImportWebAsync(CancellationToken cancellationToken) => RunBusyAsync(async () =>
    {
        var path = await _picker.PickWebDirectoryAsync(cancellationToken);
        if (path is null) return;
        var result = await _controller.ImportWebAsync(path, cancellationToken);
        if (!result.Result.Succeeded)
        {
            Notice = FromFailure(result.Result);
            return;
        }

        await RefreshAsync(cancellationToken);
        Notice = OpenNotice(UserNoticeSeverity.Success, result.Result.UserMessage ?? "Đã thêm web wallpaper.", null);
    }, cancellationToken);

    public Task RevalidateAsync(LibraryItemViewModel item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunBusyAsync(async () =>
        {
            var result = await _controller.RevalidateAsync(item.Id, cancellationToken);
            if (!result.Succeeded)
            {
                Notice = FromFailure(result);
                return;
            }
            await RefreshAsync(cancellationToken);
            Notice = OpenNotice(UserNoticeSeverity.Success, result.UserMessage ?? "Đã kiểm tra lại wallpaper.", null);
        }, cancellationToken);
    }

    public async Task RemoveAsync(LibraryItemViewModel item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (IsBusy) return;
        Notice = UserNotice.Closed;
        bool confirmed;
        try
        {
            confirmed = await _dialogs.ConfirmRemoveAsync(item.Name, item.IsAssigned, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            Notice = GenericFailure();
            return;
        }

        if (!confirmed) return;
        await RunBusyAsync(async () =>
        {
            var result = await _controller.RemoveAsync(item.Id, item.IsAssigned, cancellationToken);
            if (!result.Succeeded)
            {
                Notice = FromFailure(result);
                return;
            }
            await RefreshAsync(cancellationToken);
            Notice = OpenNotice(UserNoticeSeverity.Success, result.UserMessage ?? "Đã xóa wallpaper.", null);
        }, cancellationToken);
    }

    public Task SetNetworkPermissionAsync(
        LibraryItemViewModel item,
        bool enabled,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunBusyAsync(async () =>
        {
            var result = await _controller.SetNetworkPermissionAsync(item.Id, enabled, cancellationToken);
            if (!result.Succeeded)
            {
                Notice = FromFailure(result);
                OnPropertyChanged(nameof(FilteredItems));
                return;
            }
            await RefreshAsync(cancellationToken);
            Notice = OpenNotice(UserNoticeSeverity.Success, result.UserMessage ?? "Đã cập nhật quyền mạng.", null);
        }, cancellationToken);
    }

    public Task OpenSourceLocationAsync(LibraryItemViewModel item, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return RunBusyAsync(async () =>
        {
            var result = await _dialogs.OpenSourceLocationAsync(item.Item, cancellationToken);
            if (!result.Succeeded) Notice = FromFailure(result);
        }, cancellationToken);
    }

    public void UseWallpaper(LibraryItemViewModel item)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanUse)
        {
            Notice = OpenNotice(
                UserNoticeSeverity.Warning,
                "Wallpaper chưa sẵn sàng. Hãy kiểm tra lại nguồn trước khi sử dụng.",
                item.Validation.DiagnosticCode);
            return;
        }
        UseWallpaperRequested?.Invoke(item.Id);
    }

    public void DismissNotice() => Notice = UserNotice.Closed;

    public void Replace(LibrarySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _assignedIds = snapshot.AssignedIds.ToHashSet();
        Items.Clear();
        foreach (var item in snapshot.Items)
        {
            Items.Add(new LibraryItemViewModel(item, _assignedIds.Contains(item.Definition.Id)));
        }
        OnPropertyChanged(nameof(FilteredItems));
    }

    private async Task RefreshAsync(CancellationToken cancellationToken) =>
        Replace(await _controller.GetLibraryAsync(cancellationToken));

    private async Task RunBusyAsync(Func<Task> operation, CancellationToken cancellationToken)
    {
        if (IsBusy) return;
        IsBusy = true;
        Notice = UserNotice.Closed;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Notice = UserNotice.Closed;
        }
        catch
        {
            Notice = GenericFailure();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static UserNotice FromFailure(CommandResult result)
    {
        var presentation = UserErrorPresenter.Create(result.ErrorCode, result.UserMessage);
        return OpenNotice(
            UserNoticeSeverity.Error,
            presentation.DetailedMessage,
            presentation.DiagnosticCode);
    }

    private static UserNotice GenericFailure()
    {
        var presentation = UserErrorPresenter.Create("library.operation.failed");
        return OpenNotice(UserNoticeSeverity.Error, presentation.DetailedMessage, presentation.DiagnosticCode);
    }

    private static UserNotice OpenNotice(
        UserNoticeSeverity severity,
        string message,
        string? code) => new(true, severity, message, code);

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        OnPropertyChanged(propertyName);
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
