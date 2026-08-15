using System.Security.Cryptography;
using System.Text;

namespace VibeWallpaper.Engine.Sources;

public static class DirectoryFingerprintService
{
    public static async Task<string> ComputeAsync(string root, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(root)) throw new ArgumentException("Absolute root required.", nameof(root));
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (!Directory.Exists(canonical)) throw new DirectoryNotFoundException(canonical);
        var builder = new StringBuilder();
        foreach (var file in Directory.EnumerateFiles(canonical, "*", SearchOption.TopDirectoryOnly).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = new FileInfo(file);
            builder.Append(Path.GetRelativePath(canonical, file)).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
            if (string.Equals(info.Name, "index.html", StringComparison.OrdinalIgnoreCase))
            {
                await using var stream = File.OpenRead(file);
                var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
                builder.Append(Convert.ToHexString(hash)).Append('\n');
            }
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }
}
