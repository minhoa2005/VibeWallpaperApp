namespace VibeWallpaper.Engine.Rendering.Video;

public sealed record VideoRendererOptions
{
    public static VideoRendererOptions Default { get; } = new();

    public VideoRendererOptions(bool suspendWhenThrottled = false) =>
        SuspendWhenThrottled = suspendWhenThrottled;

    /// <summary>
    /// Maps an unsupported throttle request to a real decoder pause. When false, playback
    /// continues normally; the renderer never claims an unmeasured exact frame rate.
    /// </summary>
    public bool SuspendWhenThrottled { get; }
}
