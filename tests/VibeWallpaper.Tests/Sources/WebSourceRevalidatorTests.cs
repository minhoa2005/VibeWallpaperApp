using VibeWallpaper.Engine.Sources;

namespace VibeWallpaper.Tests.Sources;

public sealed class WebSourceRevalidatorTests
{
    [Fact]
    public async Task Fingerprint_ChangesWhenEntryPointChanges()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vibe-web-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "one", TestContext.Current.CancellationToken);
            var before = await DirectoryFingerprintService.ComputeAsync(root, TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "two", TestContext.Current.CancellationToken);
            var after = await DirectoryFingerprintService.ComputeAsync(root, TestContext.Current.CancellationToken);
            Assert.NotEqual(before, after);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task Validate_RequiresIndexHtmlInsideRoot()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "vibe-web-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            var missing = await WebSourceRevalidator.ValidateAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(WebSourceValidationStatus.MissingEntryPoint, missing.Status);
            await File.WriteAllTextAsync(Path.Combine(root, "index.html"), "ok", TestContext.Current.CancellationToken);
            var available = await WebSourceRevalidator.ValidateAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(WebSourceValidationStatus.Available, available.Status);
        }
        finally { Directory.Delete(root, true); }
    }
}
