using System.ComponentModel;
using VibeWallpaper.Engine.Monitors;
using VibeWallpaper.Engine.Native;

namespace VibeWallpaper.Tests.Monitors;

public sealed class DisplayConfigTopologyServiceTests
{
    [Fact]
    public void EdidParser_WhenBaseBlockContainsIdentity_ExtractsManufacturerProductAndSerial()
    {
        var edid = new byte[128];
        byte[] header = [0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00];
        header.CopyTo(edid, 0);
        edid[8] = 0x04;
        edid[9] = 0x6d;
        edid[10] = 0x2a;
        edid[11] = 0x00;
        edid[12] = 0x40;
        edid[13] = 0xe2;
        edid[14] = 0x01;
        edid[15] = 0x00;
        edid[127] = unchecked((byte)(0 - edid.Take(127).Sum(value => value)));

        var identity = EdidIdentityParser.TryParse(edid);

        Assert.NotNull(identity);
        Assert.Equal("ACM", identity.Manufacturer);
        Assert.Equal((ushort)42, identity.ProductCode);
        Assert.Equal((uint)123456, identity.SerialNumber);
    }

    [Fact]
    public void Capture_WhenEdidNativeSeamProvidesIdentity_PreservesCompleteEdidEvidence()
    {
        var path = new DisplayConfigPath(
            42, 1, 7, 1, @"\\.\DISPLAY1", "Panel", @"\\?\DISPLAY#ACM002A#SERIAL#{GUID}",
            "SERIAL", null, null, 5);
        var monitor = NativeMonitor();
        var service = new DisplayConfigTopologyService(
            new StubPathSource([path]),
            new StubMonitorSource([monitor]),
            new StubEdidSource(new EdidIdentity("ACM", 42, 123456)),
            new StubDesktopProbe(true));

        var snapshot = service.Capture();

        var evidence = Assert.Single(Assert.Single(snapshot.LogicalOutputs).TargetEvidence);
        Assert.Equal("ACM", evidence.EdidManufacturer);
        Assert.Equal((ushort)42, evidence.EdidProductCode);
        Assert.Equal((uint)123456, evidence.EdidSerialNumber);
    }

    [Fact]
    public void Capture_WhenNoInteractiveDesktop_ThrowsExplicitUnavailableReasonWithoutQueryingNativeTopology()
    {
        var service = new DisplayConfigTopologyService(
            new ThrowingPathSource(),
            new StubMonitorSource([]),
            new StubEdidSource(null),
            new StubDesktopProbe(false));

        var exception = Assert.Throws<DisplayTopologyUnavailableException>(() => service.Capture());

        Assert.Equal(DisplayTopologyUnavailableReason.NoInteractiveDesktop, exception.Reason);
    }

    [Fact]
    public void Capture_WhenInteractiveDesktopNativeQueryFails_ReportsCaptureFailureInsteadOfUnavailableDesktop()
    {
        var service = new DisplayConfigTopologyService(
            new ThrowingPathSource(),
            new StubMonitorSource([]),
            new StubEdidSource(null),
            new StubDesktopProbe(true));

        var exception = Assert.Throws<DisplayTopologyCaptureException>(() => service.Capture());

        Assert.IsType<Win32Exception>(exception.InnerException);
    }

    private static NativeMonitor NativeMonitor() =>
        new(1, @"\\.\DISPLAY1", new(0, 0, 1920, 1080), new(0, 0, 1920, 1040), 96, true);

    private sealed class StubPathSource(IReadOnlyList<DisplayConfigPath> paths) : IDisplayConfigPathSource
    {
        public IReadOnlyList<DisplayConfigPath> QueryActivePaths() => paths;
    }

    private sealed class ThrowingPathSource : IDisplayConfigPathSource
    {
        public IReadOnlyList<DisplayConfigPath> QueryActivePaths() =>
            throw new Win32Exception(5, "DisplayConfig denied.");
    }

    private sealed class StubMonitorSource(IReadOnlyList<NativeMonitor> monitors) : IPhysicalMonitorSource
    {
        public IReadOnlyList<NativeMonitor> EnumeratePhysicalPixelMonitors() => monitors;
    }

    private sealed class StubEdidSource(EdidIdentity? identity) : IEdidIdentitySource
    {
        public EdidIdentity? Find(string? monitorDevicePath, string? targetInstanceId) => identity;
    }

    private sealed class StubDesktopProbe(bool available) : IInteractiveDesktopProbe
    {
        public bool IsAvailable => available;
    }
}
