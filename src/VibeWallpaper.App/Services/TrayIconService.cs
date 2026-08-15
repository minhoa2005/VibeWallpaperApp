#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeWallpaper.App.Services;

public sealed record TrayIconMenu(Action Open, Action TogglePause, Action Exit);

public interface ITrayIconBackend : IDisposable
{
    event Action<bool, string?>? AvailabilityChanged;
    bool TryCreate(TrayIconMenu menu);
    void SetPaused(bool paused);
}

public sealed class TrayIconService : IDisposable
{
    private readonly ITrayIconBackend _backend;
    private bool _paused;
    private bool _disposed;

    public TrayIconService(ITrayIconBackend? backend = null)
    {
        _backend = backend ?? new ShellNotifyIconBackend();
        _backend.AvailabilityChanged += HandleAvailabilityChanged;
    }

    public event Action? OpenRequested;
    public event Action? PauseResumeRequested;
    public event Action? ExitRequested;
    public event Action<bool, string?>? AvailabilityChanged;

    public bool IsAvailable { get; private set; }
    public bool IsPaused => _paused;
    public string? LastError { get; private set; }

    public bool TryStart()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsAvailable)
        {
            return true;
        }

        try
        {
            IsAvailable = _backend.TryCreate(new TrayIconMenu(
                () => OpenRequested?.Invoke(),
                () => PauseResumeRequested?.Invoke(),
                () => ExitRequested?.Invoke()));
            LastError = IsAvailable
                ? null
                : "The notification-area icon could not be created.";
        }
        catch (Exception exception)
        {
            IsAvailable = false;
            LastError = exception.Message;
        }

        return IsAvailable;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        if (IsAvailable)
        {
            _backend.SetPaused(paused);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        IsAvailable = false;
        _backend.AvailabilityChanged -= HandleAvailabilityChanged;
        _backend.Dispose();
    }

    private void HandleAvailabilityChanged(bool available, string? error)
    {
        if (_disposed)
        {
            return;
        }

        if (available)
        {
            try
            {
                _backend.SetPaused(_paused);
            }
            catch (Exception exception)
            {
                available = false;
                error = exception.Message;
            }
        }

        IsAvailable = available;
        LastError = available ? null : error ?? "The notification-area icon could not be restored.";
        AvailabilityChanged?.Invoke(IsAvailable, LastError);
    }

    private sealed class ShellNotifyIconBackend : ITrayIconBackend
    {
        private const uint WmAppTray = 0x8001;
        private const uint WmLButtonDoubleClick = 0x0203;
        private const uint WmRButtonUp = 0x0205;
        private const uint WmContextMenu = 0x007B;
        private const uint NimAdd = 0x00000000;
        private const uint NimDelete = 0x00000002;
        private const uint NimSetVersion = 0x00000004;
        private const uint NotifyIconVersion4 = 4;
        private const uint NifMessage = 0x00000001;
        private const uint NifIcon = 0x00000002;
        private const uint NifTip = 0x00000004;
        private const uint MfString = 0x00000000;
        private const uint MfSeparator = 0x00000800;
        private const uint TpmRightButton = 0x0002;
        private const uint TpmReturnCommand = 0x0100;
        private const uint OpenCommand = 1;
        private const uint PauseCommand = 2;
        private const uint ExitCommand = 3;
        private const int IdiApplication = 32512;
        private static readonly string WindowClassName = $"VibeWallpaper.Tray.{Environment.ProcessId}";
        private static readonly WndProc WindowProcedure = HandleWindowMessage;
        private static readonly uint TaskbarCreatedMessage = RegisterWindowMessageW("TaskbarCreated");
        private static readonly Dictionary<nint, ShellNotifyIconBackend> Instances = [];
        private static readonly object ClassGate = new();
        private static ushort _windowClass;
        private TrayIconMenu? _menu;
        private nint _window;
        private nint _icon;
        private bool _paused;

        public event Action<bool, string?>? AvailabilityChanged;

        public bool TryCreate(TrayIconMenu menu)
        {
            ArgumentNullException.ThrowIfNull(menu);
            EnsureWindowClass();
            _menu = menu;
            _window = CreateWindowExW(
                0,
                WindowClassName,
                "Vibe Wallpaper Tray",
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                GetModuleHandleW(null),
                0);
            if (_window == 0)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            lock (Instances)
            {
                Instances[_window] = this;
            }

            _icon = LoadIconW(0, (nint)IdiApplication);
            if (!TryAddIcon())
            {
                Dispose();
                throw new Win32Exception(
                    Marshal.GetLastPInvokeError(),
                    "Shell_NotifyIcon(NIM_ADD/NIM_SETVERSION) failed.");
            }

            return true;
        }

        public void SetPaused(bool paused) => _paused = paused;

        public void Dispose()
        {
            if (_window == 0)
            {
                return;
            }

            var data = CreateNotifyData();
            _ = Shell_NotifyIconW(NimDelete, ref data);
            lock (Instances)
            {
                Instances.Remove(_window);
            }

            _ = DestroyWindow(_window);
            _window = 0;
            _icon = 0;
            _menu = null;
        }

        private NotifyIconData CreateNotifyData() => new()
        {
            cbSize = (uint)Marshal.SizeOf<NotifyIconData>(),
            hWnd = _window,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip,
            uCallbackMessage = WmAppTray,
            hIcon = _icon,
            szTip = "Vibe Wallpaper",
        };

        private bool TryAddIcon()
        {
            var data = CreateNotifyData();
            if (!Shell_NotifyIconW(NimAdd, ref data))
            {
                return false;
            }

            data.uTimeoutOrVersion = NotifyIconVersion4;
            if (Shell_NotifyIconW(NimSetVersion, ref data))
            {
                return true;
            }

            _ = Shell_NotifyIconW(NimDelete, ref data);
            return false;
        }

        private void ShowMenu()
        {
            var popup = CreatePopupMenu();
            if (popup == 0)
            {
                return;
            }

            try
            {
                _ = AppendMenuW(popup, MfString, OpenCommand, "Open");
                _ = AppendMenuW(popup, MfString, PauseCommand, _paused ? "Resume all" : "Pause all");
                _ = AppendMenuW(popup, MfSeparator, 0, null);
                _ = AppendMenuW(popup, MfString, ExitCommand, "Exit");
                _ = GetCursorPos(out var point);
                _ = SetForegroundWindow(_window);
                var command = TrackPopupMenuEx(
                    popup,
                    TpmRightButton | TpmReturnCommand,
                    point.X,
                    point.Y,
                    _window,
                    0);
                switch (command)
                {
                    case OpenCommand:
                        _menu?.Open();
                        break;
                    case PauseCommand:
                        _menu?.TogglePause();
                        break;
                    case ExitCommand:
                        _menu?.Exit();
                        break;
                }
            }
            finally
            {
                _ = DestroyMenu(popup);
            }
        }

        private static nint HandleWindowMessage(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            ShellNotifyIconBackend? instance;
            lock (Instances)
            {
                Instances.TryGetValue(hwnd, out instance);
            }

            if (message == WmAppTray && instance is not null)
            {
                var mouseMessage = unchecked((uint)lParam.ToInt64()) & 0xFFFF;
                if (mouseMessage == WmLButtonDoubleClick)
                {
                    instance._menu?.Open();
                    return 0;
                }

                if (mouseMessage is WmRButtonUp or WmContextMenu)
                {
                    instance.ShowMenu();
                    return 0;
                }
            }

            if (message == TaskbarCreatedMessage && instance is not null)
            {
                try
                {
                    if (instance.TryAddIcon())
                    {
                        instance.AvailabilityChanged?.Invoke(true, null);
                    }
                    else
                    {
                        instance.AvailabilityChanged?.Invoke(
                            false,
                            "Shell_NotifyIcon(NIM_ADD/NIM_SETVERSION) failed while restoring the tray icon.");
                    }
                }
                catch (Exception exception)
                {
                    instance.AvailabilityChanged?.Invoke(false, exception.Message);
                }

                return 0;
            }

            return DefWindowProcW(hwnd, message, wParam, lParam);
        }

        private static void EnsureWindowClass()
        {
            lock (ClassGate)
            {
                if (_windowClass != 0)
                {
                    return;
                }

                var windowClass = new WindowClass
                {
                    lpfnWndProc = WindowProcedure,
                    hInstance = GetModuleHandleW(null),
                    lpszClassName = WindowClassName,
                };
                _windowClass = RegisterClassW(ref windowClass);
                if (_windowClass == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WindowClass
        {
            public uint style;
            [MarshalAs(UnmanagedType.FunctionPtr)] public WndProc lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct NotifyIconData
        {
            public uint cbSize;
            public nint hWnd;
            public uint uID;
            public uint uFlags;
            public uint uCallbackMessage;
            public nint hIcon;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
            public uint dwState;
            public uint dwStateMask;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string? szInfo;
            public uint uTimeoutOrVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string? szInfoTitle;
            public uint dwInfoFlags;
            public Guid guidItem;
            public nint hBalloonIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Point
        {
            public int X;
            public int Y;
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate nint WndProc(nint hwnd, uint message, nuint wParam, nint lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint RegisterWindowMessageW(string message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern ushort RegisterClassW(ref WindowClass windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateWindowExW(
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

        [DllImport("user32.dll")]
        private static extern nint DefWindowProcW(nint hwnd, uint message, nuint wParam, nint lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll")]
        private static extern nint LoadIconW(nint instance, nint iconName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern nint GetModuleHandleW(string? moduleName);

        [DllImport("user32.dll")]
        private static extern nint CreatePopupMenu();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool AppendMenuW(nint menu, uint flags, nuint identifier, string? text);

        [DllImport("user32.dll")]
        private static extern uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint window, nint parameters);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyMenu(nint menu);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetForegroundWindow(nint window);
    }
}
