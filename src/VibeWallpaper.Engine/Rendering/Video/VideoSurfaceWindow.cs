using VibeWallpaper.Engine.Native;

namespace VibeWallpaper.Engine.Rendering.Video;

internal interface IVideoSurfaceWindowFactory
{
    IVideoSurfaceWindow Create(nint parentHwnd);
}

internal interface IVideoSurfaceWindow : IDisposable
{
    nint Hwnd { get; }
    void Show();
}

internal sealed class VideoSurfaceWindowFactory : IVideoSurfaceWindowFactory
{
    internal static VideoSurfaceWindowFactory Instance { get; } = new();

    private VideoSurfaceWindowFactory()
    {
    }

    public IVideoSurfaceWindow Create(nint parentHwnd) =>
        new VideoSurfaceWindow(NativeDesktopWindowApi.Instance, parentHwnd);
}

internal sealed class VideoSurfaceWindow : IVideoSurfaceWindow
{
    private readonly NativeDesktopWindowApi _windows;

    internal VideoSurfaceWindow(NativeDesktopWindowApi windows, nint parentHwnd)
    {
        _windows = windows ?? throw new ArgumentNullException(nameof(windows));
        Hwnd = _windows.CreateRendererWindow(parentHwnd);
    }

    public nint Hwnd { get; private set; }

    public void Show() => _windows.SetWindowVisible(Hwnd, true);

    public void Dispose()
    {
        var hwnd = Hwnd;
        if (hwnd == 0)
        {
            return;
        }

        Hwnd = 0;
        _windows.DestroyWindow(hwnd);
    }
}
