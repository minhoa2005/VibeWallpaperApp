using System.Text;

namespace VibeWallpaper.App.Services;

public sealed class StartupFailureReporter
{
    private readonly string _directory;
    private readonly string _path;

    public StartupFailureReporter(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _path = Path.Combine(_directory, "startup-failure.log");
    }

    public async Task ReportAsync(Exception exception, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            Directory.CreateDirectory(_directory);
            var entry = $"{DateTimeOffset.UtcNow:O} [STARTUP] {exception}{Environment.NewLine}";
            await File.AppendAllTextAsync(_path, entry, Encoding.UTF8, cancellationToken);
        }
        catch
        {
            // Startup reporting is the final safety net and must never mask the original failure.
        }
    }
}
