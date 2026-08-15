using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Interaction;

public enum InteractionExitReason
{
    Escape,
    SessionLocked,
    DisplayOff,
    SystemSleeping,
    DesktopContextLost,
    TopologyInvalidated,
    RendererDisposed,
    ForwardingWatchdog,
    InactivityTimeout,
    ApplicationExit,
}

public interface IInteractionOverlay
{
    void Destroy();
}

public interface IInteractionOverlayFactory
{
    IInteractionOverlay Create(MonitorIdentity output);
}
