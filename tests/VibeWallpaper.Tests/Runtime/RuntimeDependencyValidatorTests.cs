using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class RuntimeDependencyValidatorTests
{
    [Fact]
    public void MissingVideo_DoesNotDisableWeb()
    {
        var report = RuntimeDependencyValidator.Validate(
            new RuntimeDependencyPaths("C:\\missing\\libvlc.dll", "C:\\missing\\libvlccore.dll", "C:\\missing\\plugins", null),
            webAvailable: true);

        Assert.False(report.Video.Available);
        Assert.Equal("runtime.video.missing", report.Video.FailureCode);
        Assert.True(report.Web.Available);
    }

    [Fact]
    public void MissingWeb_DoesNotDisableVideo()
    {
        var report = RuntimeDependencyValidator.Validate(
            new RuntimeDependencyPaths("C:\\missing\\libvlc.dll", "C:\\missing\\libvlccore.dll", "C:\\missing\\plugins", null),
            webAvailable: false);

        Assert.False(report.Video.Available);
        Assert.False(report.Web.Available);
        Assert.Equal("runtime.web.unavailable", report.Web.FailureCode);
    }
}
