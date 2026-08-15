using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeWallpaper.Engine.Activity;

internal sealed record ActivityNativeSystemFacts(
    bool RunningOnBattery,
    bool BatterySaverEnabled,
    bool RemoteDesktopSession);

internal interface IActivitySystemFactsNativeApi
{
    ActivityNativeSystemFacts Capture();
}

internal interface IActivityWindowContextNativeApi
{
    nint GetParent(nint hwnd);
}

public sealed class WindowsActivitySystemFactsProvider : IActivitySystemFactsProvider, IActivityEvidenceConsumer
{
    private readonly IActivitySystemFactsNativeApi _native;
    private int _sessionLocked;
    private int _displayOff;
    private int _systemSleeping;

    public WindowsActivitySystemFactsProvider()
        : this(NativeActivityFactsApi.Instance)
    {
    }

    internal WindowsActivitySystemFactsProvider(IActivitySystemFactsNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public ActivitySystemFacts Capture()
    {
        var native = _native.Capture();
        return new ActivitySystemFacts(
            Volatile.Read(ref _sessionLocked) != 0,
            Volatile.Read(ref _displayOff) != 0,
            Volatile.Read(ref _systemSleeping) != 0,
            native.RunningOnBattery,
            native.BatterySaverEnabled,
            native.RemoteDesktopSession);
    }

    public void Apply(ActivityEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        switch (evidence.Kind)
        {
            case ActivityEvidenceKind.SessionLocked:
                SetSessionLocked(true);
                break;
            case ActivityEvidenceKind.SessionUnlocked:
                SetSessionLocked(false);
                break;
            case ActivityEvidenceKind.DisplayOff:
                SetDisplayOff(true);
                break;
            case ActivityEvidenceKind.DisplayOn:
                SetDisplayOff(false);
                break;
            case ActivityEvidenceKind.SystemSleeping:
                SetSystemSleeping(true);
                break;
            case ActivityEvidenceKind.SystemResumed:
                SetSystemSleeping(false);
                break;
        }
    }

    internal void SetSessionLocked(bool value) => Volatile.Write(ref _sessionLocked, value ? 1 : 0);
    internal void SetDisplayOff(bool value) => Volatile.Write(ref _displayOff, value ? 1 : 0);
    internal void SetSystemSleeping(bool value) => Volatile.Write(ref _systemSleeping, value ? 1 : 0);
}

public sealed class WindowsActivityWindowContextProvider : IActivityWindowContextProvider
{
    private readonly Func<IReadOnlyCollection<nint>> _ownedWindows;
    private readonly IActivityWindowContextNativeApi _native;

    public WindowsActivityWindowContextProvider(Func<IReadOnlyCollection<nint>> ownedWindows)
        : this(ownedWindows, NativeActivityFactsApi.Instance)
    {
    }

    internal WindowsActivityWindowContextProvider(
        Func<IReadOnlyCollection<nint>> ownedWindows,
        IActivityWindowContextNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(ownedWindows);
        ArgumentNullException.ThrowIfNull(native);
        _ownedWindows = ownedWindows;
        _native = native;
    }

    public ActivityWindowContext Capture()
    {
        var owned = _ownedWindows()?.ToArray() ?? throw new InvalidOperationException("Owned window capture returned null.");
        var first = owned.FirstOrDefault(static hwnd => hwnd != 0);
        if (first == 0) throw new InvalidOperationException("At least one wallpaper host window is required.");
        var desktopHost = _native.GetParent(first);
        if (desktopHost == 0) throw new Win32Exception("The wallpaper host no longer has a Desktop parent.");
        return new ActivityWindowContext(desktopHost, owned);
    }
}

internal sealed partial class NativeActivityFactsApi : IActivitySystemFactsNativeApi, IActivityWindowContextNativeApi
{
    private const int SmRemoteSession = 0x1000;

    internal static NativeActivityFactsApi Instance { get; } = new();

    private NativeActivityFactsApi() { }

    public ActivityNativeSystemFacts Capture()
    {
        if (!GetSystemPowerStatus(out var status))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "GetSystemPowerStatus failed.");
        return new ActivityNativeSystemFacts(
            status.AcLineStatus == 0,
            status.SystemStatusFlag != 0,
            GetSystemMetrics(SmRemoteSession) != 0);
    }

    public nint GetParent(nint hwnd) => NativeGetParent(hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        internal byte AcLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll", EntryPoint = "GetParent")]
    private static partial nint NativeGetParent(nint hwnd);
}
