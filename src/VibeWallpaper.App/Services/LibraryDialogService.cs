using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.App.Services;

public sealed class LibraryDialogService : ILibraryDialogService
{
    private readonly IRemoveDialogPresenter _dialogPresenter;
    private readonly IProcessLauncher _processLauncher;

    public LibraryDialogService(Func<XamlRoot?> xamlRootProvider)
        : this(
            new WinUiRemoveDialogPresenter(xamlRootProvider),
            new SystemProcessLauncher())
    {
    }

    internal LibraryDialogService(
        IRemoveDialogPresenter dialogPresenter,
        IProcessLauncher processLauncher)
    {
        ArgumentNullException.ThrowIfNull(dialogPresenter);
        ArgumentNullException.ThrowIfNull(processLauncher);
        _dialogPresenter = dialogPresenter;
        _processLauncher = processLauncher;
    }

    public Task<bool> ConfirmRemoveAsync(
        string wallpaperName,
        bool isAssigned,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(wallpaperName))
            throw new ArgumentException("A wallpaper name is required.", nameof(wallpaperName));

        var assignmentText = isAssigned
            ? " Wallpaper đang được sử dụng; cấu hình màn hình liên quan cũng sẽ được gỡ."
            : string.Empty;
        var request = new RemoveDialogRequest(
            "Xóa wallpaper?",
            $"Xóa “{wallpaperName.Trim()}” khỏi thư viện?{assignmentText}\n\n" +
            "Tệp hoặc thư mục nguồn sẽ không bị xóa hay thay đổi.",
            "Xóa khỏi thư viện",
            "Hủy",
            isAssigned);
        return _dialogPresenter.ShowAsync(request, cancellationToken);
    }

    public Task<CommandResult> OpenSourceLocationAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var request = CreateExplorerRequest(item.Definition.Source);
            _processLauncher.Launch(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new CommandResult(true, null, "Đã mở vị trí nguồn."));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return Task.FromResult(new CommandResult(
                false,
                "library.source.open_failed",
                "Không thể mở vị trí tệp nguồn."));
        }
    }

    private static ProcessLaunchRequest CreateExplorerRequest(WallpaperSource source)
    {
        var (storedPath, selectFile) = source switch
        {
            VideoSource video => (video.FilePath, true),
            WebSource web => (web.DirectoryPath, false),
            _ => throw new InvalidOperationException("This wallpaper source has no file-system location."),
        };

        if (!Path.IsPathFullyQualified(storedPath))
            throw new InvalidOperationException("The stored source path is not absolute.");

        var canonical = selectFile
            ? Path.GetFullPath(storedPath)
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(storedPath));
        if (!string.Equals(canonical, storedPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The stored source path is not canonical.");

        IReadOnlyList<string> arguments = selectFile
            ? ["/select,", canonical]
            : [canonical];
        return new ProcessLaunchRequest("explorer.exe", false, arguments);
    }

    private sealed class WinUiRemoveDialogPresenter : IRemoveDialogPresenter
    {
        private readonly Func<XamlRoot?> _xamlRootProvider;

        public WinUiRemoveDialogPresenter(Func<XamlRoot?> xamlRootProvider)
        {
            ArgumentNullException.ThrowIfNull(xamlRootProvider);
            _xamlRootProvider = xamlRootProvider;
        }

        public async Task<bool> ShowAsync(
            RemoveDialogRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var root = _xamlRootProvider()
                ?? throw new InvalidOperationException("The library page is not attached to a XAML root.");
            var dialog = new ContentDialog
            {
                XamlRoot = root,
                Title = request.Title,
                Content = request.Content,
                PrimaryButtonText = request.PrimaryButtonText,
                CloseButtonText = request.CloseButtonText,
                DefaultButton = ContentDialogButton.Close,
            };
            var result = await dialog.ShowAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return result == ContentDialogResult.Primary;
        }
    }

    private sealed class SystemProcessLauncher : IProcessLauncher
    {
        public void Launch(ProcessLaunchRequest request)
        {
            var start = new ProcessStartInfo(request.FileName)
            {
                UseShellExecute = request.UseShellExecute,
            };
            foreach (var argument in request.Arguments) start.ArgumentList.Add(argument);
            _ = Process.Start(start) ?? throw new InvalidOperationException("Explorer did not start.");
        }
    }
}
