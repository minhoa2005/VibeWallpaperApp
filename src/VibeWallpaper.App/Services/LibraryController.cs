using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Diagnostics;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.App.Services;

public sealed class LibraryController : ILibraryController
{
    private readonly IWallpaperImportPreparer _preparer;
    private readonly ILibraryStateAuthority _authority;
    private readonly ILogSink? _log;
    private readonly Action<LibrarySnapshot>? _libraryChanged;

    public LibraryController(
        IWallpaperImportPreparer preparer,
        ILibraryStateAuthority authority,
        ILogSink? log = null,
        Action<LibrarySnapshot>? libraryChanged = null)
    {
        ArgumentNullException.ThrowIfNull(preparer);
        ArgumentNullException.ThrowIfNull(authority);
        _preparer = preparer;
        _authority = authority;
        _log = log;
        _libraryChanged = libraryChanged;
    }

    public Task<LibrarySnapshot> GetLibraryAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ToAppSnapshot(_authority.GetLibrarySnapshot()));
    }

    public Task<ImportResult> ImportVideoAsync(
        string absolutePath,
        CancellationToken cancellationToken) =>
        ImportAsync(
            token => _preparer.PrepareVideoAsync(absolutePath, token),
            "Đã thêm video vào thư viện.",
            cancellationToken);

    public Task<ImportResult> ImportWebAsync(
        string absoluteDirectory,
        CancellationToken cancellationToken) =>
        ImportAsync(
            token => _preparer.PrepareWebAsync(absoluteDirectory, token),
            "Đã thêm web wallpaper vào thư viện.",
            cancellationToken);

    public async Task<CommandResult> RevalidateAsync(
        WallpaperId id,
        CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _authority.GetLibrarySnapshot().Items
                .FirstOrDefault(candidate => candidate.Definition.Id == id);
            if (item is null) return Failure("library.item.missing");
            var updated = await _preparer.RevalidateAsync(
                item, cancellationToken).ConfigureAwait(false);
            var snapshot = await _authority.ReplaceLibraryItemAsync(
                updated, cancellationToken).ConfigureAwait(false);
            Publish(snapshot);
            return new CommandResult(true, null, "Đã kiểm tra lại wallpaper.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WallpaperImportException exception)
        {
            return Failure(exception.DiagnosticCode);
        }
        catch (VibeWallpaper.Engine.Import.Video.LibVlcRuntimeUnavailableException)
        {
            return Failure("video.runtime.unavailable");
        }
        catch (LibraryStateException exception)
        {
            return Failure(exception.Code);
        }
        catch (Exception exception)
        {
            await LogUnexpectedAsync("revalidate", exception).ConfigureAwait(false);
            return Failure("library.operation.failed");
        }
    }

    public async Task<CommandResult> RemoveAsync(
        WallpaperId id,
        bool clearAssignments,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _authority.RemoveLibraryItemAsync(
                id, clearAssignments, cancellationToken).ConfigureAwait(false);
            Publish(snapshot);
            return new CommandResult(true, null, "Đã xóa wallpaper khỏi thư viện.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LibraryStateException exception)
        {
            return Failure(exception.Code);
        }
        catch (Exception exception)
        {
            await LogUnexpectedAsync("remove", exception).ConfigureAwait(false);
            return Failure("library.operation.failed");
        }
    }

    public async Task<CommandResult> SetNetworkPermissionAsync(
        WallpaperId id,
        bool enabled,
        CancellationToken cancellationToken)
    {
        var item = _authority.GetLibrarySnapshot().Items
            .FirstOrDefault(candidate => candidate.Definition.Id == id);
        if (item is null) return Failure("library.item.missing");
        if (item.Definition.Source is not WebSource)
            return Failure("library.network.web_required");

        try
        {
            var snapshot = await _authority.SetWebNetworkPermissionAsync(
                id, enabled, cancellationToken).ConfigureAwait(false);
            Publish(snapshot);
            return new CommandResult(
                true,
                null,
                enabled ? "Đã cho phép truy cập mạng." : "Đã chặn truy cập mạng.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (LibraryStateException exception)
        {
            return Failure(exception.Code);
        }
        catch (Exception exception)
        {
            await LogUnexpectedAsync("network-permission", exception).ConfigureAwait(false);
            return Failure("library.operation.failed");
        }
    }

    private async Task<ImportResult> ImportAsync(
        Func<CancellationToken, Task<WallpaperLibraryItem>> prepare,
        string successMessage,
        CancellationToken cancellationToken)
    {
        try
        {
            var item = await prepare(cancellationToken).ConfigureAwait(false);
            var snapshot = await _authority.AddLibraryItemAsync(
                item, cancellationToken).ConfigureAwait(false);
            Publish(snapshot);
            return new ImportResult(
                new CommandResult(true, null, successMessage),
                item);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (WallpaperImportException exception)
        {
            return new ImportResult(Failure(exception.DiagnosticCode), null);
        }
        catch (VibeWallpaper.Engine.Import.Video.LibVlcRuntimeUnavailableException)
        {
            return new ImportResult(Failure("video.runtime.unavailable"), null);
        }
        catch (LibraryStateException exception)
        {
            return new ImportResult(Failure(exception.Code), null);
        }
        catch (Exception exception)
        {
            await LogUnexpectedAsync("import", exception).ConfigureAwait(false);
            return new ImportResult(Failure("library.operation.failed"), null);
        }
    }

    private async ValueTask LogUnexpectedAsync(string operation, Exception exception)
    {
        if (_log is null) return;
        try
        {
            await _log.WriteAsync(
                "error",
                $"Library {operation} failed.",
                exception).ConfigureAwait(false);
        }
        catch
        {
            // A diagnostic failure must not replace the user-facing library result.
        }
    }

    private static LibrarySnapshot ToAppSnapshot(LibraryStateSnapshot snapshot) =>
        new(snapshot.Version, snapshot.Items.ToArray(), snapshot.AssignedIds.ToHashSet());

    private void Publish(LibraryStateSnapshot snapshot)
    {
        if (_libraryChanged is null) return;
        try
        {
            _libraryChanged(ToAppSnapshot(snapshot));
        }
        catch
        {
            // The mutation is already committed; a presentation refresh cannot roll it back.
        }
    }

    private static CommandResult Failure(string code) => code switch
    {
        "video.source.unsupported" =>
            new(false, code, "Định dạng video chưa được hỗ trợ."),
        "video.source.missing" =>
            new(false, code, "Không tìm thấy tệp video."),
        "video.source.directory" =>
            new(false, code, "Hãy chọn một tệp video."),
        "video.probe.invalid" =>
            new(false, code, "Video không thể phát hoặc đã bị hỏng."),
        "video.source.changed_during_import" =>
            new(false, code, "Video đã thay đổi trong lúc nhập. Hãy thử lại."),
        "video.helper.timeout" =>
            new(false, code, "Quá trình đọc thông tin video mất quá nhiều thời gian."),
        "video.runtime.unavailable" =>
            new(false, code, "Thiếu hoặc không thể khởi động thành phần xử lý video của ứng dụng."),
        "video.helper.crashed" =>
            new(false, code, "Thành phần kiểm tra video đã dừng đột ngột."),
        "video.helper.invalid_response" =>
            new(false, code, "Ứng dụng nhận được kết quả kiểm tra video không hợp lệ."),
        "web.source.invalid_root" =>
            new(false, code, "Thư mục web wallpaper không hợp lệ."),
        "web.source.missing" =>
            new(false, code, "Không tìm thấy thư mục web wallpaper."),
        "web.entry.missing" =>
            new(false, code, "Thư mục wallpaper phải chứa index.html."),
        "library.item.duplicate" =>
            new(false, code, "Wallpaper này đã có trong thư viện."),
        "library.item.missing" =>
            new(false, code, "Wallpaper không còn trong thư viện."),
        "library.item.assigned" =>
            new(false, code, "Wallpaper đang được sử dụng trên một màn hình."),
        "library.network.web_required" =>
            new(false, code, "Chỉ web wallpaper mới có quyền truy cập mạng."),
        "library.runtime.cleanup_failed" =>
            new(false, code, "Đã xóa wallpaper nhưng chưa dọn xong renderer."),
        _ => new(false, code, "Không thể hoàn tất thao tác với wallpaper."),
    };
}
