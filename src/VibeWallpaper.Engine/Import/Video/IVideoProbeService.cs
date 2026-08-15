using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Import.Video;

public interface IVideoProbeService
{
    Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken);
}

public sealed class VideoProbeException : Exception
{
    public VideoProbeException(string diagnosticCode, string message)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(diagnosticCode))
            throw new ArgumentException("A diagnostic code is required.", nameof(diagnosticCode));
        DiagnosticCode = diagnosticCode;
    }

    public VideoProbeException(string diagnosticCode, string message, Exception innerException)
        : base(message, innerException)
    {
        if (string.IsNullOrWhiteSpace(diagnosticCode))
            throw new ArgumentException("A diagnostic code is required.", nameof(diagnosticCode));
        DiagnosticCode = diagnosticCode;
    }

    public string DiagnosticCode { get; }
}

public interface IVideoThumbnailService
{
    Task<string?> GenerateAsync(
        WallpaperId wallpaperId,
        string absolutePath,
        VideoMetadata metadata,
        CancellationToken cancellationToken);
}
