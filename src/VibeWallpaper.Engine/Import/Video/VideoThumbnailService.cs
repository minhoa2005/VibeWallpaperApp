using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Import.Video;

public interface IVideoSnapshotter
{
    Task CaptureAsync(
        string absoluteSourcePath,
        string absoluteDestinationPath,
        uint width,
        uint height,
        CancellationToken cancellationToken);
}

public sealed class VideoThumbnailService : IVideoThumbnailService
{
    private const uint MaximumWidth = 512;
    private const uint MaximumHeight = 288;
    private readonly IVideoSnapshotter _snapshotter;
    private readonly string _cacheDirectory;

    public VideoThumbnailService()
        : this(
            new LibVlcVideoSnapshotter(),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
    {
    }

    public VideoThumbnailService(IVideoSnapshotter snapshotter, string localAppData)
    {
        ArgumentNullException.ThrowIfNull(snapshotter);
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new ArgumentException("A local application-data path is required.", nameof(localAppData));
        _snapshotter = snapshotter;
        _cacheDirectory = Path.GetFullPath(Path.Combine(
            localAppData, "VibeWallpaper", "cache", "thumbnails"));
    }

    public async Task<string?> GenerateAsync(
        WallpaperId wallpaperId,
        string absolutePath,
        VideoMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
            throw new ArgumentException("An absolute video path is required.", nameof(absolutePath));
        Directory.CreateDirectory(_cacheDirectory);
        var destination = Path.GetFullPath(Path.Combine(_cacheDirectory, $"{wallpaperId.Value:N}.png"));
        var scale = Math.Min(1d, Math.Min((double)MaximumWidth / metadata.Width, (double)MaximumHeight / metadata.Height));
        var width = Math.Max(1u, (uint)Math.Round(metadata.Width * scale));
        var height = Math.Max(1u, (uint)Math.Round(metadata.Height * scale));
        var complete = false;
        try
        {
            await _snapshotter.CaptureAsync(
                Path.GetFullPath(absolutePath), destination, width, height, cancellationToken).ConfigureAwait(false);
            complete = true;
            return destination;
        }
        finally
        {
            if (!complete && File.Exists(destination)) File.Delete(destination);
        }
    }
}

public sealed class LibVlcVideoSnapshotter : IVideoSnapshotter
{
    private readonly LibVlcProcessClient _client;
    private readonly string _cacheRoot;

    public LibVlcVideoSnapshotter(
        string? helperPath = null,
        TimeSpan? timeout = null,
        string? cacheRoot = null)
    {
        _client = new LibVlcProcessClient(helperPath, timeout);
        _cacheRoot = Path.GetFullPath(cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeWallpaper", "cache", "thumbnails"));
    }

    public async Task CaptureAsync(
        string absoluteSourcePath,
        string absoluteDestinationPath,
        uint width,
        uint height,
        CancellationToken cancellationToken)
    {
        var result = await _client.RunAsync(
            "thumbnail", absoluteSourcePath, absoluteDestinationPath, width, height, _cacheRoot, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
            throw new VideoProbeException(
                result.DiagnosticCode ?? "video.thumbnail.failed",
                result.Message ?? "Thumbnail helper failed.");
    }
}
