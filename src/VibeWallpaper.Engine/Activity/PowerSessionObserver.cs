using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VibeWallpaper.Engine.Native;

namespace VibeWallpaper.Engine.Activity;

public sealed class PowerSessionObserver : IActivityObserver
{
    private readonly object _gate = new();
    private readonly IActivityEvidenceSink _sink;
    private readonly IReadOnlyList<IDisposable> _registrations;
    private readonly Func<PowerSessionObserver, IDisposable>? _register;
    private IDisposable? _nativeRegistration;
    private bool _disposed;

    public PowerSessionObserver(IActivityEvidenceSink sink, IEnumerable<IDisposable>? registrations = null)
        : this(sink, registrations, null)
    {
    }

    public PowerSessionObserver(
        IActivityEvidenceSink sink,
        WindowsActivitySystemFactsProvider facts,
        IEnumerable<IDisposable>? registrations = null)
        : this(sink, registrations, NativePowerSessionRegistration.Register)
    {
        ArgumentNullException.ThrowIfNull(facts);
    }

    internal PowerSessionObserver(
        IActivityEvidenceSink sink,
        Func<PowerSessionObserver, IDisposable> register)
        : this(sink, null, register)
    {
    }

    private PowerSessionObserver(
        IActivityEvidenceSink sink,
        IEnumerable<IDisposable>? registrations,
        Func<PowerSessionObserver, IDisposable>? register)
    {
        ArgumentNullException.ThrowIfNull(sink);
        _sink = sink;
        _register = register;
        _registrations = registrations?.ToArray() ?? [];
        if (_registrations.Any(static item => item is null)) throw new ArgumentException("Registrations cannot contain null.", nameof(registrations));
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _nativeRegistration ??= _register?.Invoke(this);
        }
    }

    public void SessionLocked() => Publish(ActivityEvidenceKind.SessionLocked);
    public void SessionUnlocked() => Publish(ActivityEvidenceKind.SessionUnlocked);
    public void SystemSleeping() => Publish(ActivityEvidenceKind.SystemSleeping);
    public void SystemResumed() => Publish(ActivityEvidenceKind.SystemResumed);
    public void DisplayOff() => Publish(ActivityEvidenceKind.DisplayOff);
    public void DisplayOn() => Publish(ActivityEvidenceKind.DisplayOn);
    public void PowerChanged() => Publish(ActivityEvidenceKind.PowerChanged);
    public void RemoteDesktopChanged() => Publish(ActivityEvidenceKind.RemoteDesktopChanged);

    public void Dispose()
    {
        IDisposable? nativeRegistration;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            nativeRegistration = _nativeRegistration;
            _nativeRegistration = null;
        }

        nativeRegistration?.Dispose();
        foreach (var registration in _registrations.Reverse()) registration.Dispose();
    }

    private void Publish(ActivityEvidenceKind kind)
    {
        lock (_gate)
        {
            if (!_disposed) _sink.Enqueue(new ActivityEvidence(kind));
        }
    }
}

internal sealed partial class NativePowerSessionRegistration : IDisposable
{
    private const uint WmClose = 0x0010;
    private const uint WmNcDestroy = 0x0082;
    private const uint WmPowerBroadcast = 0x0218;
    private const uint WmWtsSessionChange = 0x02B1;
    private const nuint PbtApmSuspend = 0x0004;
    private const nuint PbtApmResumeSuspend = 0x0007;
    private const nuint PbtApmResumeAutomatic = 0x0012;
    private const nuint PbtPowerSettingChange = 0x8013;
    private const nuint WtsRemoteConnect = 0x3;
    private const nuint WtsRemoteDisconnect = 0x4;
    private const nuint WtsSessionLock = 0x7;
    private const nuint WtsSessionUnlock = 0x8;
    private const uint DeviceNotifyWindowHandle = 0;
    private static readonly Guid ConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid PowerSavingStatus = new("E00958C0-C213-4ACE-AC77-FECCED2EEEA5");
    private static readonly Guid AcDcPowerSource = new("5D3E9A59-E9D5-4B00-A6BD-FF34FF516548");
    private static readonly ConcurrentDictionary<nint, PowerSessionObserver> Observers = new();
    private static readonly object ClassGate = new();
    private static readonly string ClassName = $"VibeWallpaper.Activity.Power.{Environment.ProcessId}";
    private static bool s_classRegistered;

    private readonly Thread _thread;
    private readonly TaskCompletionSource<nint> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private nint _window;
    private int _disposed;

    private NativePowerSessionRegistration(PowerSessionObserver observer)
    {
        _thread = new Thread(() => Run(observer))
        {
            IsBackground = true,
            Name = "VibeWallpaper power/session observer",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _window = _ready.Task.GetAwaiter().GetResult();
    }

    internal static IDisposable Register(PowerSessionObserver observer) => new NativePowerSessionRegistration(observer);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        var window = Volatile.Read(ref _window);
        if (window != 0) _ = NativeMethods.PostMessage(window, WmClose, 0, 0);
        _thread.Join();
    }

    private void Run(PowerSessionObserver observer)
    {
        var notifications = new List<nint>();
        nint hwnd = 0;
        try
        {
            EnsureWindowClass();
            hwnd = User32.CreateWindowEx(0, ClassName, string.Empty, 0, 0, 0, 0, 0, new nint(-3), 0, Kernel32.GetModuleHandle(null), 0);
            if (hwnd == 0) ThrowNative("CreateWindowEx(activity notification)");
            Observers[hwnd] = observer;
            if (!NativeMethods.WtsRegisterSessionNotification(hwnd, 0)) ThrowNative("WTSRegisterSessionNotification");
            notifications.Add(RegisterPower(hwnd, ConsoleDisplayState));
            notifications.Add(RegisterPower(hwnd, PowerSavingStatus));
            notifications.Add(RegisterPower(hwnd, AcDcPowerSource));
            _ready.TrySetResult(hwnd);

            while (NativeMethods.GetMessage(out var message, 0, 0, 0) > 0)
            {
                _ = User32.TranslateMessage(in message);
                _ = User32.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            _ready.TrySetException(exception);
        }
        finally
        {
            foreach (var notification in notifications) _ = NativeMethods.UnregisterPowerSettingNotification(notification);
            if (hwnd != 0)
            {
                _ = NativeMethods.WtsUnregisterSessionNotification(hwnd);
                Observers.TryRemove(hwnd, out _);
                if (User32.IsWindow(hwnd)) _ = User32.DestroyWindow(hwnd);
            }
            Volatile.Write(ref _window, 0);
        }
    }

    private static nint RegisterPower(nint hwnd, Guid setting)
    {
        var handle = NativeMethods.RegisterPowerSettingNotification(hwnd, in setting, DeviceNotifyWindowHandle);
        if (handle == 0) ThrowNative("RegisterPowerSettingNotification");
        return handle;
    }

    private static unsafe void EnsureWindowClass()
    {
        lock (ClassGate)
        {
            if (s_classRegistered) return;
            var name = Marshal.StringToHGlobalUni(ClassName);
            try
            {
                var windowClass = new User32.WindowClassEx
                {
                    Size = (uint)Marshal.SizeOf<User32.WindowClassEx>(),
                    WindowProcedure = (nint)(delegate* unmanaged[Stdcall]<nint, uint, nuint, nint, nint>)&WindowProcedure,
                    Instance = Kernel32.GetModuleHandle(null),
                    ClassName = name,
                };
                if (User32.RegisterClassEx(in windowClass) == 0) ThrowNative("RegisterClassEx(activity notification)");
                s_classRegistered = true;
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint WindowProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (Observers.TryGetValue(hwnd, out var observer))
        {
            if (message == WmWtsSessionChange)
            {
                if (wParam == WtsSessionLock) observer.SessionLocked();
                else if (wParam == WtsSessionUnlock) observer.SessionUnlocked();
                else if (wParam is WtsRemoteConnect or WtsRemoteDisconnect) observer.RemoteDesktopChanged();
                return 0;
            }

            if (message == WmPowerBroadcast)
            {
                if (wParam == PbtApmSuspend) observer.SystemSleeping();
                else if (wParam is PbtApmResumeSuspend or PbtApmResumeAutomatic) observer.SystemResumed();
                else if (wParam == PbtPowerSettingChange && lParam != 0)
                {
                    var setting = Marshal.PtrToStructure<PowerBroadcastSetting>(lParam);
                    if (setting.PowerSetting == ConsoleDisplayState)
                    {
                        var value = Marshal.ReadInt32(lParam, Marshal.SizeOf<PowerBroadcastSetting>());
                        if (value == 0) observer.DisplayOff(); else observer.DisplayOn();
                    }
                    else observer.PowerChanged();
                }
                return 1;
            }
        }

        if (message == WmClose)
        {
            _ = User32.DestroyWindow(hwnd);
            return 0;
        }
        if (message == WmNcDestroy) User32.PostQuitMessage(0);
        return User32.DefWindowProc(hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerBroadcastSetting
    {
        internal Guid PowerSetting;
        internal uint DataLength;
    }

    private static void ThrowNative(string operation)
    {
        var error = Marshal.GetLastPInvokeError();
        throw new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }

    private static partial class NativeMethods
    {
        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        internal static partial int GetMessage(out User32.Message message, nint hwnd, uint minimum, uint maximum);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool PostMessage(nint hwnd, uint message, nuint wParam, nint lParam);

        [LibraryImport("wtsapi32.dll", EntryPoint = "WTSRegisterSessionNotification", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WtsRegisterSessionNotification(nint hwnd, uint flags);

        [LibraryImport("wtsapi32.dll", EntryPoint = "WTSUnRegisterSessionNotification")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool WtsUnregisterSessionNotification(nint hwnd);

        [LibraryImport("user32.dll", SetLastError = true)]
        internal static partial nint RegisterPowerSettingNotification(nint recipient, in Guid setting, uint flags);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool UnregisterPowerSettingNotification(nint handle);
    }
}
