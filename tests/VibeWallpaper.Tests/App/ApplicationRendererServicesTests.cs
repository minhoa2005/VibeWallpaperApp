using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Video;

namespace VibeWallpaper.Tests.App;

public sealed class ApplicationRendererServicesTests
{
    [Theory]
    [InlineData(WallpaperKind.SolidColor)]
    [InlineData(WallpaperKind.Video)]
    [InlineData(WallpaperKind.Web)]
    public void SupportsRenderer_AllComposedRendererKindsRemainAvailableDuringSourceRevalidation(
        WallpaperKind kind)
    {
        Assert.True(ApplicationRendererServices.SupportsRenderer(kind));
    }

    [Fact]
    public async Task DeferredRuntime_DoesNotInitializeNativeRuntimeUntilFirstUse()
    {
        var creations = 0;
        var inner = new RecordingRuntime();
        var runtime = new DeferredLibVlcRuntime(() =>
        {
            creations++;
            return inner;
        });

        Assert.Equal(0, creations);
        Assert.True(runtime.HardwareDecodingRequested);
        Assert.Equal(0, creations);

        using var player = runtime.CreatePlayer();

        Assert.Equal(1, creations);
        await runtime.DisposeAsync();
        Assert.True(inner.Disposed);
    }

    private sealed class RecordingRuntime : ILibVlcRuntime
    {
        public bool HardwareDecodingRequested => true;
        public string Version => "test";
        public bool Disposed { get; private set; }
        public ILibVlcPlayer CreatePlayer() => new RecordingPlayer();
        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class RecordingPlayer : ILibVlcPlayer
    {
        public nint Hwnd { set { } }
        public long TimeMilliseconds { get; set; }
        public bool IsPlaying => false;
        public bool IsMuted { get; set; }
        public int VolumePercent { get; set; }
        public event EventHandler? EndReached { add { } remove { } }
        public event EventHandler<VideoFaultEventArgs>? EncounteredError { add { } remove { } }
        public event EventHandler<VideoPlaybackProgressEventArgs>? PlaybackProgressed { add { } remove { } }
        public void ApplySourceCrop(NormalizedSourceRect crop, int videoWidth, int videoHeight) { }
        public void Open(string absolutePath, VideoMediaOpenOptions options) { }
        public void Play() { }
        public void Pause() { }
        public void Stop() { }
        public void Dispose() { }
    }
}
