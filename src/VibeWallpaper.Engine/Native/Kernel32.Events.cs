using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace VibeWallpaper.Engine.Native;

internal static partial class Kernel32
{
    [LibraryImport("kernel32.dll", EntryPoint = "CreateEventW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    internal static partial SafeWaitHandle CreateEvent(
        nint eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetEvent(SafeWaitHandle eventHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ResetEvent(SafeWaitHandle eventHandle);
}
