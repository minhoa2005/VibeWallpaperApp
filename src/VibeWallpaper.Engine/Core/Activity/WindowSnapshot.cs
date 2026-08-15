using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Core.Activity;

public sealed record WindowSnapshot(
    nint Hwnd,
    nint RootOwner,
    uint ProcessId,
    int ZOrder,
    DisplayViewport ExtendedFrameBounds,
    bool IsVisible,
    bool IsMinimized,
    bool IsCloaked,
    bool IsToolWindow,
    bool IsShellWindow,
    bool IsApplicationOwned);
