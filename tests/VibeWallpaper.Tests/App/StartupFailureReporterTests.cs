using VibeWallpaper.App.Services;

namespace VibeWallpaper.Tests.App;

public sealed class StartupFailureReporterTests
{
    [Fact]
    public async Task ReportAsync_WritesExceptionDetailsToIndependentStartupLog()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"vibe-startup-report-{Guid.NewGuid():N}");
        try
        {
            var reporter = new StartupFailureReporter(directory);

            await reporter.ReportAsync(
                new InvalidOperationException("stage exploded"),
                TestContext.Current.CancellationToken);

            var content = await File.ReadAllTextAsync(
                Path.Combine(directory, "startup-failure.log"),
                TestContext.Current.CancellationToken);
            Assert.Contains("System.InvalidOperationException", content, StringComparison.Ordinal);
            Assert.Contains("stage exploded", content, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ReportAsync_WhenStorageIsUnavailable_DoesNotThrow()
    {
        var filePath = Path.Combine(Path.GetTempPath(), $"vibe-startup-report-{Guid.NewGuid():N}.tmp");
        await File.WriteAllTextAsync(filePath, "not a directory", TestContext.Current.CancellationToken);
        try
        {
            var reporter = new StartupFailureReporter(filePath);

            var exception = await Record.ExceptionAsync(
                () => reporter.ReportAsync(
                    new InvalidOperationException("stage exploded"),
                    TestContext.Current.CancellationToken));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(filePath);
        }
    }
}
