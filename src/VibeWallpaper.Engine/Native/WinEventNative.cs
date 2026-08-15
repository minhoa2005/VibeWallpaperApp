using System.ComponentModel;
using System.Runtime.InteropServices;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Native;

internal static partial class WinEventNative
{
    internal const uint GaRootOwner = 3;
    internal const int GwlExStyle = -20;
    internal const nuint WsExToolWindow = 0x00000080;
    internal const uint DwmwaExtendedFrameBounds = 9;
    internal const uint DwmwaCloaked = 14;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventObjectLocationChange = 0x800B;

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    internal static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttribute(
        nint hwnd,
        uint attribute,
        out User32.Rect value,
        uint valueSize);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetWindowAttribute(
        nint hwnd,
        uint attribute,
        out int value,
        uint valueSize);

    internal static bool IsCloaked(nint hwnd)
    {
        var result = DwmGetWindowAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int));
        Marshal.ThrowExceptionForHR(result);
        return cloaked != 0;
    }

    internal static DisplayViewport GetExtendedFrameBounds(nint hwnd)
    {
        var result = DwmGetWindowAttribute(
            hwnd,
            DwmwaExtendedFrameBounds,
            out User32.Rect bounds,
            (uint)Marshal.SizeOf<User32.Rect>());
        Marshal.ThrowExceptionForHR(result);
        var width = checked(bounds.Right - bounds.Left);
        var height = checked(bounds.Bottom - bounds.Top);
        if (width <= 0 || height <= 0)
        {
            throw new Win32Exception($"DWM returned invalid extended frame bounds for HWND 0x{hwnd:X}.");
        }

        return new DisplayViewport(bounds.Left, bounds.Top, width, height);
    }

    internal static unsafe string GetClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* pointer = buffer)
        {
            var length = User32.GetClassName(hwnd, pointer, buffer.Length);
            return length == 0 ? string.Empty : new string(pointer, 0, length);
        }
    }
}
