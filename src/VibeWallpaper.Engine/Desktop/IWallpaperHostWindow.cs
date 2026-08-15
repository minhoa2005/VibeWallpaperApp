using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Desktop;

public interface IWallpaperHostWindow : IAsyncDisposable
{
    nint Hwnd { get; }
    MonitorIdentity Monitor { get; }
    DisplayViewport Bounds { get; }
    bool IsVisible { get; }
    void SetBounds(DisplayViewport bounds);
    void SetRendererChild(nint rendererHwnd);
    void Show();
    void Hide();
}
