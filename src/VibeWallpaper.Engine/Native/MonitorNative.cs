using System.ComponentModel;
using System.Runtime.InteropServices;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Native;

internal static unsafe partial class MonitorNative
{
    private const uint MonitorInfoPrimary = 0x00000001;
    private const int MonitorDpiTypeEffective = 0;
    private static readonly nint DpiAwarenessContextPerMonitorAwareV2 = new(-4);

    internal static IReadOnlyList<NativeMonitor> EnumeratePhysicalPixelMonitors()
    {
        var monitors = new List<NativeMonitor>();
        var previousDpiContext = SetThreadDpiAwarenessContext(DpiAwarenessContextPerMonitorAwareV2);
        RequireDpiAwarenessContext(previousDpiContext, Marshal.GetLastPInvokeError());
        try
        {
            MonitorEnumProc callback = (monitor, _, _, _) =>
            {
                var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
                if (!GetMonitorInfo(monitor, &info))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetMonitorInfo failed.");
                }

                string deviceName;
                deviceName = ReadNullTerminated(info.DeviceName, 32);

                var dpi = GetEffectiveDpi(monitor);
                monitors.Add(new(
                    monitor,
                    deviceName,
                    ToViewport(info.Monitor),
                    ToViewport(info.Work),
                    dpi,
                    (info.Flags & MonitorInfoPrimary) != 0));
                return true;
            };

            if (!EnumDisplayMonitors(0, 0, callback, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "EnumDisplayMonitors failed.");
            }
        }
        finally
        {
            if (previousDpiContext != 0)
            {
                var restoredContext = SetThreadDpiAwarenessContext(previousDpiContext);
                RequireDpiAwarenessContext(restoredContext, Marshal.GetLastPInvokeError());
            }
        }

        return monitors;
    }

    private static uint GetEffectiveDpi(nint monitor)
    {
        var result = GetDpiForMonitor(monitor, MonitorDpiTypeEffective, out var dpiX, out _);
        return RequireEffectiveDpi(result, dpiX);
    }

    internal static uint RequireEffectiveDpi(int nativeHResult, uint dpi)
    {
        if (nativeHResult < 0)
        {
            throw MonitorDpiException.FromHResult(nativeHResult);
        }

        if (dpi < 96)
        {
            throw MonitorDpiException.InvalidDpi(dpi);
        }

        return dpi;
    }

    internal static nint RequireDpiAwarenessContext(nint previousContext, int nativeErrorCode)
    {
        if (previousContext == 0)
        {
            throw MonitorDpiException.ContextFailure(nativeErrorCode);
        }

        return previousContext;
    }

    private static DisplayViewport ToViewport(Rectangle value) =>
        new(value.Left, value.Top, checked(value.Right - value.Left), checked(value.Bottom - value.Top));

    private static string ReadNullTerminated(char* value, int maximumLength)
    {
        var length = 0;
        while (length < maximumLength && value[length] != '\0')
        {
            length++;
        }

        return new string(value, 0, length);
    }

    private delegate bool MonitorEnumProc(nint monitor, nint deviceContext, nint monitorRectangle, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint deviceContext, nint clipRectangle, MonitorEnumProc callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, MonitorInfoEx* monitorInfo);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint dpiContext);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rectangle
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        internal uint Size;
        internal Rectangle Monitor;
        internal Rectangle Work;
        internal uint Flags;
        internal fixed char DeviceName[32];
    }
}

internal sealed record NativeMonitor(
    nint Handle,
    string DeviceName,
    DisplayViewport Bounds,
    DisplayViewport WorkArea,
    uint Dpi,
    bool IsPrimary);

internal sealed class MonitorDpiException : Exception
{
    private MonitorDpiException(string message, int? nativeHResult, int? nativeErrorCode)
        : base(message)
    {
        NativeHResult = nativeHResult;
        NativeErrorCode = nativeErrorCode;
    }

    internal int? NativeHResult { get; }

    internal int? NativeErrorCode { get; }

    internal static MonitorDpiException FromHResult(int nativeHResult) =>
        new($"GetDpiForMonitor failed with HRESULT 0x{nativeHResult:X8}.", nativeHResult, null);

    internal static MonitorDpiException InvalidDpi(uint dpi) =>
        new($"GetDpiForMonitor returned invalid effective DPI {dpi}.", null, null);

    internal static MonitorDpiException ContextFailure(int nativeErrorCode) =>
        new($"SetThreadDpiAwarenessContext failed with Win32 error {nativeErrorCode}.", null, nativeErrorCode);
}
