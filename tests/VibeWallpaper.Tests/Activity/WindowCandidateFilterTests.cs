using VibeWallpaper.Engine.Activity;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Tests.Activity;

public sealed class WindowCandidateFilterTests
{
    public static TheoryData<WindowSnapshot> RejectedSnapshots => new()
    {
        Snapshot(0),
        Snapshot(10) with { ProcessId = 0 },
        Snapshot(10) with { IsVisible = false },
        Snapshot(10) with { IsMinimized = true },
        Snapshot(10) with { IsCloaked = true },
        Snapshot(10) with { IsToolWindow = true },
        Snapshot(10) with { IsShellWindow = true },
        Snapshot(10) with { IsApplicationOwned = true },
        Snapshot(10, zOrder: 4),
    };

    [Theory]
    [MemberData(nameof(RejectedSnapshots))]
    public void IsCandidate_RejectsInvalidOrNonApplicationEvidence(WindowSnapshot snapshot)
    {
        Assert.False(WindowCandidateFilter.IsCandidate(snapshot, desktopZOrder: 4));
    }

    [Fact]
    public void IsCandidate_AcceptsVisibleNormalWindowAboveDesktop()
    {
        Assert.True(WindowCandidateFilter.IsCandidate(Snapshot(11, zOrder: 3), desktopZOrder: 4));
    }

    [Fact]
    public void Capture_BeginsWithForegroundRootOwnerAndIncludesOtherWindowsAboveDesktop()
    {
        var native = new FakeWindowSnapshotNativeApi
        {
            Foreground = 11,
            Windows = [10, 22, 99, 33],
        };
        native.RootOwners[11] = 10;
        native.Bounds[10] = new(0, 0, 800, 600);
        native.Bounds[22] = new(1920, 0, 1920, 1080);
        native.Bounds[99] = new(0, 0, 3840, 1080);
        native.Bounds[33] = new(0, 0, 1920, 1080);
        var provider = new WindowSnapshotProvider(native);

        var captured = provider.Capture(99, new HashSet<nint>());

        Assert.Collection(
            captured,
            snapshot => Assert.Equal(10, snapshot.Hwnd),
            snapshot => Assert.Equal(22, snapshot.Hwnd));
    }

    [Fact]
    public void Capture_RejectsInvalidAndEveryApplicationOwnedWindowKind()
    {
        var native = new FakeWindowSnapshotNativeApi
        {
            Foreground = 41,
            Windows = [41, 42, 43, 44, 99],
        };
        native.Invalid.Add(41);
        foreach (var hwnd in native.Windows)
        {
            native.Bounds[hwnd] = new(0, 0, 1920, 1080);
        }

        var provider = new WindowSnapshotProvider(native);

        var captured = provider.Capture(99, new HashSet<nint> { 42, 43, 44 });

        Assert.Empty(captured);
    }

    private static WindowSnapshot Snapshot(nint hwnd, int zOrder = 0) =>
        new(hwnd, hwnd, 42, zOrder, new DisplayViewport(0, 0, 1920, 1080), true, false, false, false, false, false);

    private sealed class FakeWindowSnapshotNativeApi : IWindowSnapshotNativeApi
    {
        public nint Foreground { get; init; }

        public IReadOnlyList<nint> Windows { get; init; } = [];

        public Dictionary<nint, nint> RootOwners { get; } = [];

        public Dictionary<nint, DisplayViewport> Bounds { get; } = [];

        public HashSet<nint> Invalid { get; } = [];

        public nint GetForegroundWindow() => Foreground;

        public IReadOnlyList<nint> EnumerateTopLevelWindows() => Windows;

        public bool IsWindow(nint hwnd) => !Invalid.Contains(hwnd);

        public nint GetRootOwner(nint hwnd) => RootOwners.GetValueOrDefault(hwnd, hwnd);

        public uint GetProcessId(nint hwnd) => 42;

        public bool IsVisible(nint hwnd) => true;

        public bool IsMinimized(nint hwnd) => false;

        public bool IsCloaked(nint hwnd) => false;

        public bool IsToolWindow(nint hwnd) => false;

        public bool IsShellWindow(nint hwnd, nint desktopHostHwnd) => hwnd == desktopHostHwnd;

        public DisplayViewport GetExtendedFrameBounds(nint hwnd) => Bounds[hwnd];
    }
}
