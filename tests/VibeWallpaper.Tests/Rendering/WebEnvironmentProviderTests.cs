using VibeWallpaper.Engine.Rendering.Web;

namespace VibeWallpaper.Tests.Rendering;

public sealed class WebEnvironmentProviderTests
{
    [Fact]
    public async Task GetAndInvalidate_AdvanceGenerationWithoutCreatingDuplicateHandles()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibe-env-" + Guid.NewGuid().ToString("N"));
        await using var provider = new WebEnvironmentProvider(root);
        var first = await provider.GetAsync(TestContext.Current.CancellationToken);
        var same = await provider.GetAsync(TestContext.Current.CancellationToken);
        Assert.Same(first, same);
        await provider.InvalidateAsync(first.Generation, TestContext.Current.CancellationToken);
        var next = await provider.GetAsync(TestContext.Current.CancellationToken);
        Assert.True(next.Generation > first.Generation);
        Directory.Delete(root, true);
    }
}
