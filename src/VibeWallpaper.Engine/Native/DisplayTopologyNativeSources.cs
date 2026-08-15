using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace VibeWallpaper.Engine.Native;

internal interface IDisplayConfigPathSource
{
    IReadOnlyList<DisplayConfigPath> QueryActivePaths();
}

internal interface IPhysicalMonitorSource
{
    IReadOnlyList<NativeMonitor> EnumeratePhysicalPixelMonitors();
}

internal interface IEdidIdentitySource
{
    EdidIdentity? Find(string? monitorDevicePath, string? targetInstanceId);
}

internal interface IInteractiveDesktopProbe
{
    bool IsAvailable { get; }
}

internal sealed class NativeDisplayConfigPathSource : IDisplayConfigPathSource
{
    internal static NativeDisplayConfigPathSource Instance { get; } = new();

    private NativeDisplayConfigPathSource()
    {
    }

    public IReadOnlyList<DisplayConfigPath> QueryActivePaths() => DisplayConfigNative.QueryActivePaths();
}

internal sealed class NativePhysicalMonitorSource : IPhysicalMonitorSource
{
    internal static NativePhysicalMonitorSource Instance { get; } = new();

    private NativePhysicalMonitorSource()
    {
    }

    public IReadOnlyList<NativeMonitor> EnumeratePhysicalPixelMonitors() =>
        MonitorNative.EnumeratePhysicalPixelMonitors();
}

internal sealed partial class NativeInteractiveDesktopProbe : IInteractiveDesktopProbe
{
    private const uint DesktopReadObjects = 0x0001;
    private const uint DesktopSwitchDesktop = 0x0100;

    internal static NativeInteractiveDesktopProbe Instance { get; } = new();

    private NativeInteractiveDesktopProbe()
    {
    }

    public bool IsAvailable
    {
        get
        {
            if (!Environment.UserInteractive)
            {
                return false;
            }

            var desktop = OpenInputDesktop(0, false, DesktopReadObjects | DesktopSwitchDesktop);
            if (desktop == 0)
            {
                return false;
            }

            return CloseDesktop(desktop);
        }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);
}

internal sealed record EdidIdentity(string Manufacturer, ushort ProductCode, uint? SerialNumber);

internal static class EdidIdentityParser
{
    private static ReadOnlySpan<byte> Header => [0x00, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0x00];

    internal static EdidIdentity? TryParse(ReadOnlySpan<byte> edid)
    {
        if (edid.Length < 128 || !edid[..8].SequenceEqual(Header))
        {
            return null;
        }

        var checksum = 0;
        for (var index = 0; index < 128; index++)
        {
            checksum += edid[index];
        }

        if ((checksum & 0xff) != 0)
        {
            return null;
        }

        var manufacturerCode = (ushort)((edid[8] << 8) | edid[9]);
        Span<char> manufacturer = stackalloc char[3];
        manufacturer[0] = DecodeManufacturerCharacter((manufacturerCode >> 10) & 0x1f);
        manufacturer[1] = DecodeManufacturerCharacter((manufacturerCode >> 5) & 0x1f);
        manufacturer[2] = DecodeManufacturerCharacter(manufacturerCode & 0x1f);
        if (manufacturer.Contains('?'))
        {
            return null;
        }

        var productCode = (ushort)(edid[10] | (edid[11] << 8));
        var serialNumber = (uint)(edid[12] | (edid[13] << 8) | (edid[14] << 16) | (edid[15] << 24));
        return new(manufacturer.ToString(), productCode, serialNumber == 0 ? null : serialNumber);
    }

    private static char DecodeManufacturerCharacter(int value) =>
        value is >= 1 and <= 26 ? (char)('A' - 1 + value) : '?';
}

internal sealed class SetupApiEdidIdentitySource : IEdidIdentitySource
{
    private const uint DigcfPresent = 0x00000002;
    private const uint DigcfAllClasses = 0x00000004;
    private const uint DicsFlagGlobal = 0x00000001;
    private const uint DiregDev = 0x00000001;
    private const int KeyRead = 0x00020019;
    private static readonly nint InvalidHandleValue = new(-1);

    internal static SetupApiEdidIdentitySource Instance { get; } = new();

    private SetupApiEdidIdentitySource()
    {
    }

    public EdidIdentity? Find(string? monitorDevicePath, string? targetInstanceId)
    {
        var requestedPath = NormalizeDeviceInstance(monitorDevicePath);
        var requestedTarget = string.IsNullOrWhiteSpace(targetInstanceId) ? null : targetInstanceId.Trim();
        if (requestedPath is null && requestedTarget is null)
        {
            return null;
        }

        try
        {
            var matches = EnumeratePresentMonitorEdids()
                .Where(item =>
                    (requestedPath is not null && string.Equals(item.NormalizedInstanceId, requestedPath, StringComparison.OrdinalIgnoreCase)) ||
                    (requestedPath is null && requestedTarget is not null && item.NormalizedInstanceId.EndsWith($"\\{requestedTarget}", StringComparison.OrdinalIgnoreCase)))
                .Select(item => item.Identity)
                .Take(2)
                .ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<SetupApiEdid> EnumeratePresentMonitorEdids()
    {
        var deviceInfoSet = SetupDiGetClassDevs(0, "DISPLAY", 0, DigcfPresent | DigcfAllClasses);
        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs failed for monitor EDID enumeration.");
        }

        try
        {
            var results = new List<SetupApiEdid>();
            for (uint index = 0; ; index++)
            {
                var deviceInfo = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(deviceInfoSet, index, ref deviceInfo))
                {
                    const int ErrorNoMoreItems = 259;
                    var error = Marshal.GetLastPInvokeError();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "SetupDiEnumDeviceInfo failed for monitor EDID enumeration.");
                }

                var instanceId = GetDeviceInstanceId(deviceInfoSet, ref deviceInfo);
                var identity = ReadEdidIdentity(deviceInfoSet, ref deviceInfo);
                if (identity is not null)
                {
                    results.Add(new(instanceId.ToUpperInvariant(), identity));
                }
            }

            return results;
        }
        finally
        {
            _ = SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static string GetDeviceInstanceId(nint deviceInfoSet, ref SpDevInfoData deviceInfo)
    {
        var buffer = new char[512];
        if (!SetupDiGetDeviceInstanceId(deviceInfoSet, ref deviceInfo, buffer, buffer.Length, out _))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetDeviceInstanceId failed for a monitor.");
        }

        var terminator = Array.IndexOf(buffer, '\0');
        return new string(buffer, 0, terminator < 0 ? buffer.Length : terminator);
    }

    private static EdidIdentity? ReadEdidIdentity(nint deviceInfoSet, ref SpDevInfoData deviceInfo)
    {
        var registryHandle = SetupDiOpenDevRegKey(
            deviceInfoSet,
            ref deviceInfo,
            DicsFlagGlobal,
            0,
            DiregDev,
            KeyRead);
        if (registryHandle == InvalidHandleValue)
        {
            return null;
        }

        using var safeHandle = new SafeRegistryHandle(registryHandle, ownsHandle: true);
        using var key = RegistryKey.FromHandle(safeHandle);
        return key.GetValue("EDID") is byte[] edid ? EdidIdentityParser.TryParse(edid) : null;
    }

    private static string? NormalizeDeviceInstance(string? monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath))
        {
            return null;
        }

        var normalized = monitorDevicePath.Trim();
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            normalized = normalized[4..];
        }

        var interfaceClass = normalized.IndexOf("#{", StringComparison.Ordinal);
        if (interfaceClass >= 0)
        {
            normalized = normalized[..interfaceClass];
        }

        return normalized.Replace('#', '\\').ToUpperInvariant();
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint SetupDiGetClassDevs(nint classGuid, string enumerator, nint parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(nint deviceInfoSet, uint memberIndex, ref SpDevInfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        [Out] char[] deviceInstanceId,
        int deviceInstanceIdSize,
        out int requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern nint SetupDiOpenDevRegKey(
        nint deviceInfoSet,
        ref SpDevInfoData deviceInfoData,
        uint scope,
        uint hardwareProfile,
        uint keyType,
        int desiredAccess);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(nint deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        internal uint Size;
        internal Guid ClassGuid;
        internal uint DeviceInstance;
        internal nint Reserved;
    }

    private sealed record SetupApiEdid(string NormalizedInstanceId, EdidIdentity Identity);
}
