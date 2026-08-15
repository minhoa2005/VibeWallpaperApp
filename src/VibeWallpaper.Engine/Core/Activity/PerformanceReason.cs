namespace VibeWallpaper.Engine.Core.Activity;

public enum PerformanceReason
{
    FullscreenCovered,
    MaximizedCovered,
    Battery,
    BatterySaver,
    SessionLocked,
    DisplayOff,
    SystemSleeping,
    RemoteDesktop,
    UserPaused,
    RendererFault,
    ExplorerUnavailable,
    MonitorDisconnected,
    Shutdown,
}

public enum PerformanceReasonOwner
{
    Activity,
    User,
    Renderer,
    DesktopHost,
    Topology,
    Shutdown,
}
