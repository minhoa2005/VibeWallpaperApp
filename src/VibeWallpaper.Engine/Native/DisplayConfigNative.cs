using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeWallpaper.Engine.Native;

internal static unsafe partial class DisplayConfigNative
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const uint QdcVirtualModeAware = 0x00000010;
    private const int ErrorInsufficientBuffer = 122;
    private const int DisplayConfigDeviceInfoGetSourceName = 1;
    private const int DisplayConfigDeviceInfoGetTargetName = 2;
    private const int MaximumBufferRetries = 5;

    internal static IReadOnlyList<DisplayConfigPath> QueryActivePaths()
    {
        const uint flags = QdcOnlyActivePaths | QdcVirtualModeAware;

        for (var attempt = 0; attempt < MaximumBufferRetries; attempt++)
        {
            var result = GetDisplayConfigBufferSizes(flags, out var pathCount, out var modeCount);
            if (result != 0)
            {
                throw new Win32Exception(result, "GetDisplayConfigBufferSizes failed.");
            }

            var paths = new PathInfo[pathCount];
            var modes = new ModeInfo[modeCount];
            fixed (PathInfo* pathPointer = paths)
            fixed (ModeInfo* modePointer = modes)
            {
                result = QueryDisplayConfig(flags, ref pathCount, pathPointer, ref modeCount, modePointer, 0);
            }

            if (result == ErrorInsufficientBuffer)
            {
                continue;
            }

            if (result != 0)
            {
                throw new Win32Exception(result, "QueryDisplayConfig failed.");
            }

            var captured = new DisplayConfigPath[pathCount];
            for (var index = 0; index < pathCount; index++)
            {
                var path = paths[index];
                var sourceName = TryGetSourceName(path.Source.AdapterId, path.Source.Id);
                var targetName = TryGetTargetName(path.Target.AdapterId, path.Target.Id);
                captured[index] = new(
                    path.Source.AdapterId.ToInt64(),
                    path.Source.Id,
                    path.Target.Id,
                    path.Target.Rotation,
                    sourceName,
                    targetName?.FriendlyName,
                    targetName?.DevicePath,
                    targetName?.TargetInstanceId,
                    targetName?.EdidManufacturer,
                    targetName?.EdidProductCode,
                    targetName?.ConnectorInstance);
            }

            return captured;
        }

        throw new Win32Exception(ErrorInsufficientBuffer, "Display configuration changed repeatedly while it was being captured.");
    }

    private static string? TryGetSourceName(Luid adapterId, uint sourceId)
    {
        var request = new SourceDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DisplayConfigDeviceInfoGetSourceName,
                Size = (uint)sizeof(SourceDeviceName),
                AdapterId = adapterId,
                Id = sourceId,
            },
        };

        var result = DisplayConfigGetDeviceInfo(&request.Header);
        if (result != 0)
        {
            return null;
        }

        return ReadNullTerminated(request.ViewGdiDeviceName, 32);
    }

    private static TargetDeviceIdentity? TryGetTargetName(Luid adapterId, uint targetId)
    {
        var request = new TargetDeviceName
        {
            Header = new DeviceInfoHeader
            {
                Type = DisplayConfigDeviceInfoGetTargetName,
                Size = (uint)sizeof(TargetDeviceName),
                AdapterId = adapterId,
                Id = targetId,
            },
        };

        var result = DisplayConfigGetDeviceInfo(&request.Header);
        if (result != 0)
        {
            return null;
        }

        string friendlyName;
        string devicePath;
        friendlyName = ReadNullTerminated(request.MonitorFriendlyDeviceName, 64);
        devicePath = ReadNullTerminated(request.MonitorDevicePath, 128);

        return new(
            NullIfEmpty(friendlyName),
            NullIfEmpty(devicePath),
            ParseTargetInstanceId(devicePath),
            DecodeEdidManufacturer(request.EdidManufactureId),
            request.EdidProductCodeId,
            request.ConnectorInstance);
    }

    private static string? ParseTargetInstanceId(string devicePath)
    {
        if (string.IsNullOrWhiteSpace(devicePath))
        {
            return null;
        }

        var parts = devicePath.Split('#', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 3 ? parts[2] : null;
    }

    private static string? DecodeEdidManufacturer(ushort encoded)
    {
        if (encoded == 0)
        {
            return null;
        }

        Span<char> value = stackalloc char[3];
        value[0] = (char)('A' - 1 + ((encoded >> 10) & 0x1f));
        value[1] = (char)('A' - 1 + ((encoded >> 5) & 0x1f));
        value[2] = (char)('A' - 1 + (encoded & 0x1f));
        return value.ToString();
    }

    private static string ReadNullTerminated(char* value, int maximumLength)
    {
        var length = 0;
        while (length < maximumLength && value[length] != '\0')
        {
            length++;
        }

        return new string(value, 0, length);
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [LibraryImport("user32.dll")]
    private static partial int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [LibraryImport("user32.dll")]
    private static partial int QueryDisplayConfig(
        uint flags,
        ref uint numPathArrayElements,
        PathInfo* pathInfoArray,
        ref uint numModeInfoArrayElements,
        ModeInfo* modeInfoArray,
        nint currentTopologyId);

    [LibraryImport("user32.dll")]
    private static partial int DisplayConfigGetDeviceInfo(DeviceInfoHeader* requestPacket);

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        internal uint LowPart;
        internal int HighPart;

        internal readonly long ToInt64() => unchecked(((long)HighPart << 32) | LowPart);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rational
    {
        internal uint Numerator;
        internal uint Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathSourceInfo
    {
        internal Luid AdapterId;
        internal uint Id;
        internal uint ModeInfoIndex;
        internal uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathTargetInfo
    {
        internal Luid AdapterId;
        internal uint Id;
        internal uint ModeInfoIndex;
        internal uint OutputTechnology;
        internal uint Rotation;
        internal uint Scaling;
        internal Rational RefreshRate;
        internal uint ScanLineOrdering;
        internal int TargetAvailable;
        internal uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PathInfo
    {
        internal PathSourceInfo Source;
        internal PathTargetInfo Target;
        internal uint Flags;
    }

    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct ModeInfo
    {
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DeviceInfoHeader
    {
        internal int Type;
        internal uint Size;
        internal Luid AdapterId;
        internal uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SourceDeviceName
    {
        internal DeviceInfoHeader Header;
        internal fixed char ViewGdiDeviceName[32];
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TargetDeviceName
    {
        internal DeviceInfoHeader Header;
        internal uint Flags;
        internal uint OutputTechnology;
        internal ushort EdidManufactureId;
        internal ushort EdidProductCodeId;
        internal uint ConnectorInstance;
        internal fixed char MonitorFriendlyDeviceName[64];
        internal fixed char MonitorDevicePath[128];
    }

    private sealed record TargetDeviceIdentity(
        string? FriendlyName,
        string? DevicePath,
        string? TargetInstanceId,
        string? EdidManufacturer,
        ushort EdidProductCode,
        uint ConnectorInstance);
}

internal sealed record DisplayConfigPath(
    long AdapterLuid,
    uint SourceId,
    uint TargetId,
    uint Rotation,
    string? GdiDeviceName,
    string? FriendlyName,
    string? MonitorDevicePath,
    string? TargetInstanceId,
    string? EdidManufacturer,
    ushort? EdidProductCode,
    uint? ConnectorInstance);
