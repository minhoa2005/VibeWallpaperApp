using System.Security.Cryptography;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Sources;

namespace VibeWallpaper.Engine.Import;

public sealed class WallpaperImportPreparer : IWallpaperImportPreparer
{
    private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".webm", ".mkv", ".mov", ".gif",
    };

    private readonly IVideoProbeService _probe;
    private readonly IVideoThumbnailService? _thumbnailService;
    private readonly TimeProvider _timeProvider;

    public WallpaperImportPreparer(
        IVideoProbeService probe,
        IVideoThumbnailService? thumbnailService = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(probe);
        _probe = probe;
        _thumbnailService = thumbnailService;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WallpaperLibraryItem> PrepareVideoAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        var path = ValidateVideoPath(sourcePath);
        var preProbeStamp = await ReadVideoStampAsync(
            path, includeFingerprint: true, cancellationToken).ConfigureAwait(false);
        VideoMetadata? metadata = null;
        VideoProbeException? probeFailure = null;
        try
        {
            metadata = await _probe.ProbeAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (VideoProbeException exception)
        {
            probeFailure = exception;
        }

        var postProbeStamp = await ReadVideoStampAsync(
            path, includeFingerprint: true, cancellationToken).ConfigureAwait(false);
        if (!Equals(preProbeStamp, postProbeStamp))
        {
            throw new WallpaperImportException(
                SourceValidationStatus.Changed,
                "video.source.changed_during_import",
                $"Video '{path}' changed while it was being probed.");
        }

        if (probeFailure is not null)
        {
            throw new WallpaperImportException(
                SourceValidationStatus.Invalid,
                probeFailure.DiagnosticCode,
                $"Video '{path}' is not playable.",
                probeFailure);
        }

        var id = WallpaperId.New();
        var thumbnailPath = _thumbnailService is null
            ? null
            : await _thumbnailService.GenerateAsync(
                id, path, metadata!, cancellationToken).ConfigureAwait(false);
        var finalStamp = await ReadVideoStampAsync(
            path, includeFingerprint: true, cancellationToken).ConfigureAwait(false);
        if (!Equals(postProbeStamp, finalStamp))
        {
            throw new WallpaperImportException(
                SourceValidationStatus.Changed,
                "video.source.changed_during_import",
                $"Video '{path}' changed while it was being imported.");
        }

        var definition = new WallpaperDefinition(
            id,
            Path.GetFileNameWithoutExtension(path),
            VideoSource.Create(path),
            FitMode.Cover,
            30,
            false,
            false,
            0,
            false);
        return new WallpaperLibraryItem(
            definition,
            thumbnailPath,
            metadata!,
            Validation(SourceValidationStatus.Available, finalStamp, null));
    }

    public async Task<WallpaperLibraryItem> PrepareWebAsync(
        string sourceDirectory,
        CancellationToken cancellationToken)
    {
        var result = await WebSourceRevalidator.ValidateAsync(
            sourceDirectory, cancellationToken).ConfigureAwait(false);
        if (result.Status != WebSourceValidationStatus.Available)
        {
            throw WebImportFailure(result.Status);
        }

        var root = result.CanonicalRoot!;
        var definition = new WallpaperDefinition(
            WallpaperId.New(),
            new DirectoryInfo(root).Name,
            WebSource.Create(root, "index.html"),
            FitMode.Cover,
            30,
            false,
            false,
            0,
            false);
        return new WallpaperLibraryItem(
            definition,
            null,
            null,
            Validation(
                SourceValidationStatus.Available,
                WebStamp(result),
                null));
    }

    public Task<WallpaperLibraryItem> RevalidateAsync(
        WallpaperLibraryItem item,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(item);
        return item.Definition.Source switch
        {
            VideoSource video => RevalidateVideoAsync(item, video, cancellationToken),
            WebSource web => RevalidateWebAsync(item, web, cancellationToken),
            _ => Task.FromResult(new WallpaperLibraryItem(
                item.Definition,
                item.ThumbnailCachePath,
                item.Video,
                Validation(
                    SourceValidationStatus.Unsupported,
                    item.Validation.Stamp,
                    "source.unsupported"))),
        };
    }

    private async Task<WallpaperLibraryItem> RevalidateVideoAsync(
        WallpaperLibraryItem item,
        VideoSource source,
        CancellationToken cancellationToken)
    {
        SourceStamp cheapStamp;
        try
        {
            cheapStamp = await ReadVideoStampAsync(
                source.FilePath, includeFingerprint: false, cancellationToken).ConfigureAwait(false);
        }
        catch (WallpaperImportException exception) when (exception.Status == SourceValidationStatus.Missing)
        {
            return WithValidation(
                item,
                Validation(SourceValidationStatus.Missing, item.Validation.Stamp, exception.DiagnosticCode),
                item.Video);
        }

        if (item.Validation.Status == SourceValidationStatus.Available &&
            StampMetadataMatches(item.Validation.Stamp, cheapStamp))
        {
            return item;
        }

        try
        {
            var before = await ReadVideoStampAsync(
                source.FilePath, includeFingerprint: true, cancellationToken).ConfigureAwait(false);
            VideoMetadata? metadata = null;
            VideoProbeException? probeFailure = null;
            try
            {
                metadata = await _probe.ProbeAsync(source.FilePath, cancellationToken).ConfigureAwait(false);
            }
            catch (VideoProbeException exception)
            {
                probeFailure = exception;
            }

            var after = await ReadVideoStampAsync(
                source.FilePath, includeFingerprint: true, cancellationToken).ConfigureAwait(false);
            if (!Equals(before, after))
            {
                return WithValidation(
                    item,
                    Validation(
                        SourceValidationStatus.Changed,
                        after,
                        "video.source.changed_during_validation"),
                    item.Video);
            }

            if (probeFailure is not null)
            {
                return WithValidation(
                    item,
                    Validation(SourceValidationStatus.Invalid, after, probeFailure.DiagnosticCode),
                    item.Video);
            }

            return WithValidation(
                item,
                Validation(SourceValidationStatus.Available, after, null),
                metadata!);
        }
        catch (WallpaperImportException exception) when (exception.Status == SourceValidationStatus.Missing)
        {
            return WithValidation(
                item,
                Validation(SourceValidationStatus.Missing, item.Validation.Stamp, exception.DiagnosticCode),
                item.Video);
        }
    }

    private async Task<WallpaperLibraryItem> RevalidateWebAsync(
        WallpaperLibraryItem item,
        WebSource source,
        CancellationToken cancellationToken)
    {
        var result = await WebSourceRevalidator.ValidateAsync(
            source.DirectoryPath, cancellationToken).ConfigureAwait(false);
        if (result.Status != WebSourceValidationStatus.Available)
        {
            var (status, code) = result.Status switch
            {
                WebSourceValidationStatus.MissingDirectory =>
                    (SourceValidationStatus.Missing, "web.source.missing"),
                WebSourceValidationStatus.MissingEntryPoint =>
                    (SourceValidationStatus.Invalid, "web.entry.missing"),
                _ => (SourceValidationStatus.Invalid, "web.source.invalid_root"),
            };
            return WithValidation(
                item,
                Validation(status, item.Validation.Stamp, code),
                item.Video);
        }

        var stamp = WebStamp(result);
        var changed = !string.Equals(
            item.Validation.Stamp?.Fingerprint,
            stamp.Fingerprint,
            StringComparison.Ordinal);
        var definition = item.Definition;
        if (changed && definition.NetworkEnabled)
        {
            definition = new WallpaperDefinition(
                definition.Id,
                definition.Name,
                definition.Source,
                definition.Fit,
                definition.TargetFps,
                false,
                definition.AudioEnabled,
                definition.VolumePercent,
                definition.InteractionEnabled);
        }

        return new WallpaperLibraryItem(
            definition,
            item.ThumbnailCachePath,
            item.Video,
            Validation(SourceValidationStatus.Available, stamp, null));
    }

    private SourceValidation Validation(
        SourceValidationStatus status,
        SourceStamp? stamp,
        string? diagnosticCode) =>
        new(status, stamp, diagnosticCode, _timeProvider.GetUtcNow());

    private static WallpaperLibraryItem WithValidation(
        WallpaperLibraryItem item,
        SourceValidation validation,
        VideoMetadata? metadata) =>
        new(item.Definition, item.ThumbnailCachePath, metadata, validation);

    private static WallpaperImportException WebImportFailure(WebSourceValidationStatus status) => status switch
    {
        WebSourceValidationStatus.MissingDirectory => new WallpaperImportException(
            SourceValidationStatus.Missing,
            "web.source.missing",
            "The web wallpaper directory does not exist."),
        WebSourceValidationStatus.MissingEntryPoint => new WallpaperImportException(
            SourceValidationStatus.Invalid,
            "web.entry.missing",
            "The web wallpaper directory must contain index.html."),
        _ => new WallpaperImportException(
            SourceValidationStatus.Invalid,
            "web.source.invalid_root",
            "The web wallpaper root is invalid."),
    };

    private static SourceStamp WebStamp(WebSourceValidationResult result) =>
        new(0, result.LatestWriteUtc!.Value, result.Fingerprint);

    private static string ValidateVideoPath(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            throw new WallpaperImportException(
                SourceValidationStatus.Missing,
                "video.source.missing",
                "A source path is required.");
        var path = Path.GetFullPath(sourcePath);
        if (Directory.Exists(path))
            throw new WallpaperImportException(
                SourceValidationStatus.Invalid,
                "video.source.directory",
                "A video file is required.");
        if (!SupportedVideoExtensions.Contains(Path.GetExtension(path)))
            throw new WallpaperImportException(
                SourceValidationStatus.Unsupported,
                "video.source.unsupported",
                "The video extension is unsupported.");
        if (!File.Exists(path))
            throw new WallpaperImportException(
                SourceValidationStatus.Missing,
                "video.source.missing",
                "The video file does not exist.");
        return path;
    }

    private static async Task<SourceStamp> ReadVideoStampAsync(
        string path,
        bool includeFingerprint,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                throw new WallpaperImportException(
                    SourceValidationStatus.Missing,
                    "video.source.missing",
                    "The video file does not exist.");
            var length = info.Length;
            var lastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero);
            string? fingerprint = null;
            if (includeFingerprint)
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read | FileShare.Delete,
                    bufferSize: 64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                fingerprint = Convert.ToHexString(hash);
                if (!File.Exists(path))
                    throw new WallpaperImportException(
                        SourceValidationStatus.Missing,
                        "video.source.missing",
                        "The video file disappeared.");
            }

            return new SourceStamp(length, lastWriteUtc, fingerprint);
        }
        catch (FileNotFoundException exception)
        {
            throw new WallpaperImportException(
                SourceValidationStatus.Missing,
                "video.source.missing",
                "The video file does not exist.",
                exception);
        }
        catch (DirectoryNotFoundException exception)
        {
            throw new WallpaperImportException(
                SourceValidationStatus.Missing,
                "video.source.missing",
                "The video directory does not exist.",
                exception);
        }
    }

    private static bool StampMetadataMatches(SourceStamp? expected, SourceStamp actual) =>
        expected is not null &&
        expected.Length == actual.Length &&
        expected.LastWriteUtc == actual.LastWriteUtc;
}
