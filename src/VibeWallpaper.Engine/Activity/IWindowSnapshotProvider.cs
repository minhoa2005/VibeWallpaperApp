using VibeWallpaper.Engine.Core.Activity;

namespace VibeWallpaper.Engine.Activity;

public interface IWindowSnapshotProvider
{
    IReadOnlyList<WindowSnapshot> Capture(
        nint desktopHostHwnd,
        IReadOnlySet<nint> applicationOwnedWindows);
}
