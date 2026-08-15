using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Native;

namespace VibeWallpaper.Engine.Activity;

internal interface IWindowSnapshotNativeApi
{
    nint GetForegroundWindow();

    IReadOnlyList<nint> EnumerateTopLevelWindows();

    bool IsWindow(nint hwnd);

    nint GetRootOwner(nint hwnd);

    uint GetProcessId(nint hwnd);

    bool IsVisible(nint hwnd);

    bool IsMinimized(nint hwnd);

    bool IsCloaked(nint hwnd);

    bool IsToolWindow(nint hwnd);

    bool IsShellWindow(nint hwnd, nint desktopHostHwnd);

    DisplayViewport GetExtendedFrameBounds(nint hwnd);
}

internal static class WindowCandidateFilter
{
    internal static bool IsCandidate(WindowSnapshot snapshot, int desktopZOrder)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return snapshot.Hwnd != 0 &&
            snapshot.ProcessId != 0 &&
            snapshot.ZOrder >= 0 &&
            snapshot.ZOrder < desktopZOrder &&
            snapshot.IsVisible &&
            !snapshot.IsMinimized &&
            !snapshot.IsCloaked &&
            !snapshot.IsToolWindow &&
            !snapshot.IsShellWindow &&
            !snapshot.IsApplicationOwned;
    }
}

public sealed class WindowSnapshotProvider : IWindowSnapshotProvider
{
    private readonly IWindowSnapshotNativeApi _native;

    public WindowSnapshotProvider()
        : this(NativeWindowSnapshotApi.Instance)
    {
    }

    internal WindowSnapshotProvider(IWindowSnapshotNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    public IReadOnlyList<WindowSnapshot> Capture(
        nint desktopHostHwnd,
        IReadOnlySet<nint> applicationOwnedWindows)
    {
        if (desktopHostHwnd == 0)
        {
            throw new ArgumentException("A Desktop host HWND is required.", nameof(desktopHostHwnd));
        }

        ArgumentNullException.ThrowIfNull(applicationOwnedWindows);
        var zOrdered = _native.EnumerateTopLevelWindows().ToArray();
        var desktopZOrder = Array.IndexOf(zOrdered, desktopHostHwnd);
        if (desktopZOrder < 0)
        {
            return [];
        }

        var foreground = _native.GetForegroundWindow();
        var foregroundRootOwner = foreground == 0 || !_native.IsWindow(foreground)
            ? 0
            : _native.GetRootOwner(foreground);
        var orderedAboveDesktop = new List<(nint Hwnd, int ZOrder)>(desktopZOrder);
        var foregroundRootZOrder = Array.IndexOf(zOrdered, foregroundRootOwner);
        if (foregroundRootZOrder >= 0 && foregroundRootZOrder < desktopZOrder)
        {
            orderedAboveDesktop.Add((foregroundRootOwner, foregroundRootZOrder));
        }

        for (var zOrder = 0; zOrder < desktopZOrder; zOrder++)
        {
            var hwnd = zOrdered[zOrder];
            if (hwnd != foregroundRootOwner)
            {
                orderedAboveDesktop.Add((hwnd, zOrder));
            }
        }

        var snapshots = new List<WindowSnapshot>(orderedAboveDesktop.Count);
        foreach (var (hwnd, zOrder) in orderedAboveDesktop)
        {
            if (!_native.IsWindow(hwnd))
            {
                continue;
            }

            try
            {
                var snapshot = new WindowSnapshot(
                    hwnd,
                    _native.GetRootOwner(hwnd),
                    _native.GetProcessId(hwnd),
                    zOrder,
                    _native.GetExtendedFrameBounds(hwnd),
                    _native.IsVisible(hwnd),
                    _native.IsMinimized(hwnd),
                    _native.IsCloaked(hwnd),
                    _native.IsToolWindow(hwnd),
                    _native.IsShellWindow(hwnd, desktopHostHwnd),
                    applicationOwnedWindows.Contains(hwnd));
                if (WindowCandidateFilter.IsCandidate(snapshot, desktopZOrder))
                {
                    snapshots.Add(snapshot);
                }
            }
            catch (ExternalException)
            {
                // A HWND can disappear between enumeration and evidence capture.
            }
        }

        return new ReadOnlyCollection<WindowSnapshot>(snapshots);
    }
}

internal sealed class NativeWindowSnapshotApi : IWindowSnapshotNativeApi
{
    internal static NativeWindowSnapshotApi Instance { get; } = new();

    private NativeWindowSnapshotApi()
    {
    }

    public nint GetForegroundWindow() => WinEventNative.GetForegroundWindow();

    public IReadOnlyList<nint> EnumerateTopLevelWindows()
    {
        var windows = new List<nint>();
        var hwnd = User32.GetTopWindow(0);
        for (var count = 0; hwnd != 0 && count < 4096; count++)
        {
            windows.Add(hwnd);
            hwnd = User32.GetWindow(hwnd, User32.GwHwndNext);
        }

        return windows;
    }

    public bool IsWindow(nint hwnd) => User32.IsWindow(hwnd);

    public nint GetRootOwner(nint hwnd) => WinEventNative.GetAncestor(hwnd, WinEventNative.GaRootOwner);

    public uint GetProcessId(nint hwnd)
    {
        _ = WinEventNative.GetWindowThreadProcessId(hwnd, out var processId);
        return processId;
    }

    public bool IsVisible(nint hwnd) => WinEventNative.IsWindowVisible(hwnd);

    public bool IsMinimized(nint hwnd) => WinEventNative.IsIconic(hwnd);

    public bool IsCloaked(nint hwnd) => WinEventNative.IsCloaked(hwnd);

    public bool IsToolWindow(nint hwnd) =>
        ((nuint)WinEventNative.GetWindowLongPtr(hwnd, WinEventNative.GwlExStyle) & WinEventNative.WsExToolWindow) != 0;

    public bool IsShellWindow(nint hwnd, nint desktopHostHwnd)
    {
        if (hwnd == desktopHostHwnd || hwnd == WinEventNative.GetShellWindow())
        {
            return true;
        }

        var className = WinEventNative.GetClassName(hwnd);
        return className is "Progman" or "WorkerW" or "SHELLDLL_DefView";
    }

    public DisplayViewport GetExtendedFrameBounds(nint hwnd) => WinEventNative.GetExtendedFrameBounds(hwnd);
}
