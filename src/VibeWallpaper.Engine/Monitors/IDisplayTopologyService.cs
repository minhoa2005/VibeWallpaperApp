using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Monitors;

public interface IDisplayTopologyService
{
    DisplayTopologySnapshot Capture();
}
