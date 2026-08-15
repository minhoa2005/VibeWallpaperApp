using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Runtime;

public enum NativeCallbackKind
{
    MediaEnded,
    PlaybackProgressed,
    LoopWatchdogExpired,
    Faulted,
}

public sealed record NativeCallbackEvent(
    RendererInstanceId RendererInstance,
    MonitorIdentity Output,
    long Generation,
    NativeCallbackKind Kind,
    string? FaultCode = null,
    string? FaultMessage = null,
    long PlaybackTimeMilliseconds = -1,
    long ProgressTimestamp = -1,
    long WatchdogSequence = -1);
