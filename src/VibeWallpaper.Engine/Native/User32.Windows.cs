using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Desktop;
using VibeWallpaper.Engine.Rendering.Solid;

namespace VibeWallpaper.Engine.Native;

internal static partial class User32
{
    internal const uint WmPaint = 0x000F;
    internal const uint WmSize = 0x0005;
    internal const uint WmEraseBackground = 0x0014;
    internal const uint WmNcDestroy = 0x0082;
    internal const uint GwHwndNext = 2;
    internal const uint SmtoAbortIfHung = 0x0002;
    internal const int GwlExtendedStyle = -20;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct PaintStruct
    {
        internal nint DeviceContext;
        internal int Erase;
        internal Rect Paint;
        internal int Restore;
        internal int IncUpdate;
        internal fixed byte Reserved[32];
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowClassEx
    {
        internal uint Size;
        internal uint Style;
        internal nint WindowProcedure;
        internal int ClassExtra;
        internal int WindowExtra;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint Background;
        internal nint MenuName;
        internal nint ClassName;
        internal nint SmallIcon;
    }

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetTopWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    internal static partial nint GetParent(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    internal static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    internal static partial nint SetWindowLongPtr(nint hwnd, int index, nint newValue);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", SetLastError = true)]
    internal static unsafe partial int GetClassName(nint hwnd, char* className, int maximumCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
    internal static partial nint SendMessageTimeout(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam,
        uint flags,
        uint timeoutMilliseconds,
        out nuint result);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out Rect rectangle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hwnd, out Rect rectangle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ScreenToClient(nint hwnd, ref Point point);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    internal static partial ushort RegisterClassEx(in WindowClassEx windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint parameter);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    internal static partial nint DefWindowProc(nint hwnd, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        nint hwnd,
        nint insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetParent(nint child, nint newParent);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint BeginPaint(nint hwnd, out PaintStruct paint);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EndPaint(nint hwnd, in PaintStruct paint);

    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint deviceContext, in Rect rectangle, nint brush);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InvalidateRect(nint hwnd, nint rectangle, [MarshalAs(UnmanagedType.Bool)] bool erase);
}

internal static partial class Gdi32
{
    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateSolidBrush(uint color);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);
}

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandle(string? moduleName);
}

internal sealed class NativeDesktopWindowApi :
    IDesktopShellWindowApi,
    IWallpaperHostWindowApi,
    ISolidRendererWindowApi
{
    private const uint ChildStyle = 0x40000000;
    private const uint ClipChildrenStyle = 0x02000000;
    private const uint ClipSiblingsStyle = 0x04000000;
    private const uint ToolWindowExtendedStyle = 0x00000080;
    private const uint NoActivateExtendedStyle = 0x08000000;
    private const nint LayeredExtendedStyle = 0x00080000;
    private const uint NoActivatePositionFlag = 0x0010;
    private const uint NoMovePositionFlag = 0x0002;
    private const uint NoSizePositionFlag = 0x0001;
    private const uint LayeredAlphaFlag = 0x00000002;
    private const int HideCommand = 0;
    private const int ShowNoActivateCommand = 4;
    private static readonly nint PerMonitorV2Context = new(-4);
    private static readonly ConcurrentDictionary<nint, uint> RendererColors = new();
    private static readonly object ClassGate = new();
    private static readonly string HostClassName = $"VibeWallpaper.Host.{Environment.ProcessId}";
    private static readonly string RendererClassName = $"VibeWallpaper.Solid.{Environment.ProcessId}";
    private static readonly SolidWindowMessageHandler SolidMessageHandler = new(NativeSolidWindowDrawingApi.Instance);
    private static bool s_classesRegistered;

    internal static NativeDesktopWindowApi Instance { get; } = new();

    private NativeDesktopWindowApi()
    {
    }

    public nint FindTopLevelWindow(string className) => User32.FindWindow(className, null);

    public bool TrySendMessageTimeout(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam,
        uint timeoutMilliseconds,
        out int errorCode)
    {
        var sent = User32.SendMessageTimeout(
            hwnd,
            message,
            wParam,
            lParam,
            User32.SmtoAbortIfHung,
            timeoutMilliseconds,
            out _) != 0;
        errorCode = sent ? 0 : Marshal.GetLastPInvokeError();
        return sent;
    }

    public nint GetExtendedWindowStyle(nint hwnd) => User32.GetWindowLongPtr(hwnd, User32.GwlExtendedStyle);

    public IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        var windows = new List<nint>();
        var current = User32.GetTopWindow(0);
        for (var count = 0; current != 0 && count < 4096; count++)
        {
            windows.Add(current);
            current = User32.GetWindow(current, User32.GwHwndNext);
        }

        return windows;
    }

    public nint FindChildWindow(nint parent, string className) =>
        User32.FindWindowEx(parent, 0, className, null);

    public nint FindNextSiblingWindow(nint hwnd, string className)
    {
        var candidate = User32.GetWindow(hwnd, User32.GwHwndNext);
        for (var count = 0; candidate != 0 && count < 4096; count++)
        {
            if (string.Equals(ReadClassName(candidate), className, StringComparison.Ordinal))
            {
                return candidate;
            }

            candidate = User32.GetWindow(candidate, User32.GwHwndNext);
        }

        return 0;
    }

    public bool IsWindow(nint hwnd) => User32.IsWindow(hwnd);

    internal string GetWindowClassName(nint hwnd) => ReadClassName(hwnd);

    public DisplayViewport GetWindowBounds(nint hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out var rectangle))
        {
            ThrowNative("GetWindowRect");
        }

        return ToViewport(rectangle);
    }

    public (int X, int Y) ScreenToClient(nint parentHwnd, int screenX, int screenY)
    {
        var point = new User32.Point { X = screenX, Y = screenY };
        if (!User32.ScreenToClient(parentHwnd, ref point))
        {
            ThrowNative("ScreenToClient");
        }

        return (point.X, point.Y);
    }

    public nint CreateHostWindow(nint parentHwnd, DisplayViewport relativeBounds)
    {
        PrepareWindowClasses();
        return CreateChild(HostClassName, parentHwnd, relativeBounds);
    }

    public void MoveWindow(nint hwnd, DisplayViewport relativeBounds) =>
        Move(hwnd, relativeBounds);

    public void SetRendererParent(nint rendererHwnd, nint hostHwnd)
    {
        if (User32.GetParent(rendererHwnd) != hostHwnd && User32.SetParent(rendererHwnd, hostHwnd) == 0)
        {
            ThrowNative("SetParent");
        }

        if (!User32.GetClientRect(hostHwnd, out var client))
        {
            ThrowNative("GetClientRect");
        }

        Move(rendererHwnd, ToViewport(client));
    }

    public void ConfigureOpaqueLayeredWindow(nint hwnd, nint insertAfter)
    {
        var extendedStyle = User32.GetWindowLongPtr(hwnd, User32.GwlExtendedStyle);
        if ((extendedStyle & LayeredExtendedStyle) == 0)
        {
            _ = User32.SetWindowLongPtr(
                hwnd,
                User32.GwlExtendedStyle,
                extendedStyle | LayeredExtendedStyle);
            var appliedStyle = User32.GetWindowLongPtr(hwnd, User32.GwlExtendedStyle);
            if ((appliedStyle & LayeredExtendedStyle) == 0)
            {
                ThrowNative("SetWindowLongPtr(WS_EX_LAYERED)");
            }
        }

        if (!User32.SetLayeredWindowAttributes(hwnd, 0, byte.MaxValue, LayeredAlphaFlag))
        {
            ThrowNative("SetLayeredWindowAttributes");
        }

        if (insertAfter != 0 && !User32.SetWindowPos(
                hwnd,
                insertAfter,
                0,
                0,
                0,
                0,
                NoMovePositionFlag | NoSizePositionFlag | NoActivatePositionFlag))
        {
            ThrowNative("SetWindowPos(raised desktop z-order)");
        }
    }

    public void SetWindowVisible(nint hwnd, bool visible) =>
        _ = User32.ShowWindow(hwnd, visible ? ShowNoActivateCommand : HideCommand);

    public void DestroyWindow(nint hwnd)
    {
        if (!User32.DestroyWindow(hwnd))
        {
            ThrowNative("DestroyWindow");
        }
    }

    public nint CreateRendererWindow(nint parentHwnd)
    {
        PrepareWindowClasses();
        if (!User32.GetClientRect(parentHwnd, out var client))
        {
            ThrowNative("GetClientRect");
        }

        var hwnd = CreateChild(RendererClassName, parentHwnd, ToViewport(client));
        RendererColors[hwnd] = 0;
        return hwnd;
    }

    public void SetColor(nint hwnd, uint color) => RendererColors[hwnd] = color;

    public void Invalidate(nint hwnd) => NativeSolidWindowDrawingApi.Instance.Invalidate(hwnd);

    public void SetVisible(nint hwnd, bool visible) => SetWindowVisible(hwnd, visible);

    private static void PrepareWindowClasses()
    {
        if (User32.SetThreadDpiAwarenessContext(PerMonitorV2Context) == 0)
        {
            ThrowNative("SetThreadDpiAwarenessContext(PMv2)");
        }

        lock (ClassGate)
        {
            if (s_classesRegistered)
            {
                return;
            }

            RegisterWindowClass(HostClassName);
            RegisterWindowClass(RendererClassName);
            s_classesRegistered = true;
        }
    }

    private static unsafe void RegisterWindowClass(string className)
    {
        var classNamePointer = Marshal.StringToHGlobalUni(className);
        try
        {
            var windowClass = new User32.WindowClassEx
            {
                Size = (uint)Marshal.SizeOf<User32.WindowClassEx>(),
                WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                Instance = Kernel32.GetModuleHandle(null),
                ClassName = classNamePointer,
            };
            if (User32.RegisterClassEx(in windowClass) == 0)
            {
                ThrowNative($"RegisterClassEx({className})");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(classNamePointer);
        }
    }

    private static nint CreateChild(string className, nint parentHwnd, DisplayViewport bounds)
    {
        var hwnd = User32.CreateWindowEx(
            ToolWindowExtendedStyle | NoActivateExtendedStyle,
            className,
            string.Empty,
            ChildStyle | ClipChildrenStyle | ClipSiblingsStyle,
            bounds.X,
            bounds.Y,
            bounds.Width,
            bounds.Height,
            parentHwnd,
            0,
            Kernel32.GetModuleHandle(null),
            0);
        if (hwnd == 0)
        {
            ThrowNative($"CreateWindowEx({className})");
        }

        return hwnd;
    }

    private static void Move(nint hwnd, DisplayViewport bounds)
    {
        if (!User32.SetWindowPos(
                hwnd,
                0,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                NoActivatePositionFlag))
        {
            ThrowNative("SetWindowPos");
        }
    }

    private static unsafe string ReadClassName(nint hwnd)
    {
        Span<char> buffer = stackalloc char[256];
        fixed (char* pointer = buffer)
        {
            var length = User32.GetClassName(hwnd, pointer, buffer.Length);
            return length == 0 ? string.Empty : new string(pointer, 0, length);
        }
    }

    private static DisplayViewport ToViewport(User32.Rect rectangle) =>
        new(rectangle.Left, rectangle.Top, rectangle.Right - rectangle.Left, rectangle.Bottom - rectangle.Top);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (message == User32.WmEraseBackground && RendererColors.ContainsKey(hwnd))
        {
            return 1;
        }

        if (message == User32.WmSize && RendererColors.ContainsKey(hwnd))
        {
            try
            {
                SolidMessageHandler.HandleResize(hwnd);
            }
            catch (Win32Exception)
            {
                return User32.DefWindowProc(hwnd, message, wParam, lParam);
            }

            return 0;
        }

        if (message == User32.WmPaint && RendererColors.TryGetValue(hwnd, out var color))
        {
            try
            {
                SolidMessageHandler.HandlePaint(hwnd, color);
            }
            catch (Win32Exception)
            {
                return User32.DefWindowProc(hwnd, message, wParam, lParam);
            }

            return 0;
        }

        if (message == User32.WmNcDestroy)
        {
            RendererColors.TryRemove(hwnd, out _);
        }

        return User32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void ThrowNative(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }
}

internal sealed class NativeSolidWindowDrawingApi : ISolidWindowDrawingApi
{
    private readonly ConcurrentDictionary<nint, User32.PaintStruct> _paintSessions = new();

    internal static NativeSolidWindowDrawingApi Instance { get; } = new();

    private NativeSolidWindowDrawingApi()
    {
    }

    public nint BeginPaint(nint hwnd)
    {
        var deviceContext = User32.BeginPaint(hwnd, out var paint);
        if (deviceContext == 0)
        {
            ThrowNative("BeginPaint");
        }

        if (!_paintSessions.TryAdd(hwnd, paint))
        {
            throw new InvalidOperationException($"A paint session is already active for HWND 0x{hwnd:X}.");
        }

        return deviceContext;
    }

    public void EndPaint(nint hwnd)
    {
        if (!_paintSessions.TryRemove(hwnd, out var paint))
        {
            throw new InvalidOperationException($"No paint session is active for HWND 0x{hwnd:X}.");
        }

        if (!User32.EndPaint(hwnd, in paint))
        {
            ThrowNative("EndPaint");
        }
    }

    public DisplayViewport GetClientBounds(nint hwnd)
    {
        if (!User32.GetClientRect(hwnd, out var client))
        {
            ThrowNative("GetClientRect");
        }

        return new DisplayViewport(
            client.Left,
            client.Top,
            client.Right - client.Left,
            client.Bottom - client.Top);
    }

    public nint CreateBrush(nint hwnd, uint color)
    {
        var brush = Gdi32.CreateSolidBrush(color);
        if (brush == 0)
        {
            ThrowNative($"CreateSolidBrush for HWND 0x{hwnd:X}");
        }

        return brush;
    }

    public void Fill(nint deviceContext, DisplayViewport bounds, nint brush)
    {
        var rectangle = new User32.Rect
        {
            Left = bounds.X,
            Top = bounds.Y,
            Right = checked(bounds.X + bounds.Width),
            Bottom = checked(bounds.Y + bounds.Height),
        };
        if (User32.FillRect(deviceContext, in rectangle, brush) == 0)
        {
            ThrowNative("FillRect");
        }
    }

    public void DeleteBrush(nint brush)
    {
        if (!Gdi32.DeleteObject(brush))
        {
            ThrowNative("DeleteObject(brush)");
        }
    }

    public void Invalidate(nint hwnd)
    {
        if (!User32.InvalidateRect(hwnd, 0, erase: false))
        {
            ThrowNative("InvalidateRect");
        }
    }

    private static void ThrowNative(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }
}
