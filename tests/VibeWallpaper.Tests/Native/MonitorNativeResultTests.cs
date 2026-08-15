using VibeWallpaper.Engine.Native;

namespace VibeWallpaper.Tests.Native;

public sealed class MonitorNativeResultTests
{
    [Fact]
    public void RequireEffectiveDpi_WhenNativeHResultFails_ThrowsTypedFailure()
    {
        var exception = Assert.Throws<MonitorDpiException>(() =>
            MonitorNative.RequireEffectiveDpi(unchecked((int)0x80070005), 0));

        Assert.Equal(unchecked((int)0x80070005), exception.NativeHResult);
    }

    [Fact]
    public void RequireDpiAwarenessContext_WhenNativeReturnsNull_ThrowsTypedFailure()
    {
        var exception = Assert.Throws<MonitorDpiException>(() =>
            MonitorNative.RequireDpiAwarenessContext(0, 5));

        Assert.Equal(5, exception.NativeErrorCode);
    }
}
