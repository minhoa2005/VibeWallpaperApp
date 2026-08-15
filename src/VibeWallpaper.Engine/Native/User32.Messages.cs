using System.Runtime.InteropServices;

namespace VibeWallpaper.Engine.Native;

internal static partial class User32
{
    internal const uint WmQuit = 0x0012;
    internal const uint WmApp = 0x8000;
    internal const uint PmNoRemove = 0x0000;
    internal const uint PmRemove = 0x0001;
    internal const uint Infinite = 0xffffffff;
    internal const uint WaitFailed = 0xffffffff;
    internal const uint QsAllInput = 0x04ff;
    internal const uint MwmoInputAvailable = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        internal int X;
        internal int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WParam;
        internal nint LParam;
        internal uint Time;
        internal Point Position;
        internal uint Private;
    }

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessage(
        out Message message,
        nint window,
        uint minimumMessage,
        uint maximumMessage,
        uint removeMessage);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static unsafe partial uint MsgWaitForMultipleObjectsEx(
        uint count,
        nint* handles,
        uint milliseconds,
        uint wakeMask,
        uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TranslateMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    internal static partial nint DispatchMessage(in Message message);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessage(
        uint threadId,
        uint message,
        nuint wParam,
        nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();
}

internal interface IThreadMessagePoster
{
    Exception? TryPost(uint threadId, uint message);
}

internal interface IThreadMessageReceiver
{
    ThreadMessageReceiveResult TryReceive(out User32.Message message);
}

internal enum ThreadMessageReceiveResult
{
    NoMessage,
    Message,
    Quit,
    Error,
}

internal sealed class NativeThreadMessageReceiver : IThreadMessageReceiver
{
    internal static NativeThreadMessageReceiver Instance { get; } = new();

    private NativeThreadMessageReceiver()
    {
    }

    public ThreadMessageReceiveResult TryReceive(out User32.Message message)
    {
        if (!User32.PeekMessage(out message, 0, 0, 0, User32.PmRemove))
        {
            return ThreadMessageReceiveResult.NoMessage;
        }

        return message.Id == User32.WmQuit
            ? ThreadMessageReceiveResult.Quit
            : ThreadMessageReceiveResult.Message;
    }
}

internal sealed class NativeThreadMessagePoster : IThreadMessagePoster
{
    internal static NativeThreadMessagePoster Instance { get; } = new();

    private NativeThreadMessagePoster()
    {
    }

    public Exception? TryPost(uint threadId, uint message) =>
        User32.PostThreadMessage(threadId, message, 0, 0)
            ? null
            : new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError());
}
