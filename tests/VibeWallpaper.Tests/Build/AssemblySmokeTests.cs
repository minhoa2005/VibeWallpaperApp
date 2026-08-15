using VibeWallpaper.Engine;

namespace VibeWallpaper.Tests.Build;

public sealed class AssemblySmokeTests
{
    [Fact]
    public void EngineAssembly_IsLoadable() =>
        Assert.Equal("VibeWallpaper.Engine", typeof(EngineAssemblyMarker).Assembly.GetName().Name);
}
