using VibeWallpaper.Engine.Core.Rendering;

namespace VibeWallpaper.Engine.Rendering.Video;

public interface ILibVlcRuntime : IAsyncDisposable
{
    ILibVlcPlayer CreatePlayer();
    bool HardwareDecodingRequested { get; }
    string Version { get; }
}

public interface ILibVlcPlayer : IDisposable
{
    nint Hwnd { set; }
    long TimeMilliseconds { get; set; }
    bool IsPlaying { get; }
    bool IsMuted { get; set; }
    int VolumePercent { get; set; }
    event EventHandler? EndReached;
    event EventHandler<VideoFaultEventArgs>? EncounteredError;
    event EventHandler<VideoPlaybackProgressEventArgs>? PlaybackProgressed;
    void ApplySourceCrop(NormalizedSourceRect crop, int videoWidth, int videoHeight);
    void Open(string absolutePath, VideoMediaOpenOptions options);
    void Play();
    void Pause();
    void Stop();
}

public sealed record VideoMediaOpenOptions
{
    public static VideoMediaOpenOptions Wallpaper { get; } = new(loop: true);

    public VideoMediaOpenOptions(bool loop) => Loop = loop;

    public bool Loop { get; }
}

public sealed class VideoFaultEventArgs : EventArgs
{
    public VideoFaultEventArgs(string faultCode, string message)
    {
        if (string.IsNullOrWhiteSpace(faultCode))
        {
            throw new ArgumentException("A fault code is required.", nameof(faultCode));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("A fault message is required.", nameof(message));
        }

        FaultCode = faultCode;
        Message = message;
    }

    public string FaultCode { get; }
    public string Message { get; }
}

public sealed class VideoPlaybackProgressEventArgs : EventArgs
{
    public VideoPlaybackProgressEventArgs(long timeMilliseconds)
    {
        if (timeMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeMilliseconds));
        }

        TimeMilliseconds = timeMilliseconds;
    }

    public long TimeMilliseconds { get; }
}

public sealed class VideoRendererControlException : InvalidOperationException
{
    public VideoRendererControlException(string operation, Exception innerException)
        : base($"Video renderer native operation '{operation}' failed.", innerException)
    {
        if (string.IsNullOrWhiteSpace(operation))
        {
            throw new ArgumentException("An operation is required.", nameof(operation));
        }

        Operation = operation;
    }

    public string Operation { get; }
}
