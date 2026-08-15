using System.Runtime.InteropServices;

namespace VibeWallpaper.Engine.Native;

internal static partial class DwmApi
{
    internal const uint TransitionsForceDisabled = 3;

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    internal static partial int SetWindowAttribute(
        nint hwnd,
        uint attribute,
        in int value,
        uint valueSize);
}
