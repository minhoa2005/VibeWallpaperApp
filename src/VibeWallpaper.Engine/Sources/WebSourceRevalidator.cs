namespace VibeWallpaper.Engine.Sources;

public enum WebSourceValidationStatus
{
    Available,
    MissingDirectory,
    MissingEntryPoint,
    InvalidRoot,
}

public sealed record WebSourceValidationResult(
    WebSourceValidationStatus Status,
    string? CanonicalRoot,
    string? Fingerprint,
    DateTimeOffset? LatestWriteUtc);

public static class WebSourceRevalidator
{
    public static async Task<WebSourceValidationResult> ValidateAsync(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root))
            return new(WebSourceValidationStatus.InvalidRoot, null, null, null);
        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new(WebSourceValidationStatus.InvalidRoot, null, null, null);
        }

        if (!Directory.Exists(canonical))
            return new(WebSourceValidationStatus.MissingDirectory, canonical, null, null);
        if ((new DirectoryInfo(canonical).Attributes & FileAttributes.ReparsePoint) != 0)
            return new(WebSourceValidationStatus.InvalidRoot, canonical, null, null);
        var entry = Path.Combine(canonical, "index.html");
        if (!File.Exists(entry))
            return new(WebSourceValidationStatus.MissingEntryPoint, canonical, null, null);
        var fingerprint = await DirectoryFingerprintService.ComputeAsync(canonical, cancellationToken).ConfigureAwait(false);
        var latestWrite = Directory.EnumerateFiles(canonical, "*", SearchOption.TopDirectoryOnly)
            .Select(path => File.GetLastWriteTimeUtc(path))
            .DefaultIfEmpty(Directory.GetLastWriteTimeUtc(canonical))
            .Max();
        return new(
            WebSourceValidationStatus.Available,
            canonical,
            fingerprint,
            new DateTimeOffset(latestWrite, TimeSpan.Zero));
    }
}
