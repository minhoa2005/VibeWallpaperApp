using System.Runtime.InteropServices;

namespace VibeWallpaper.Engine.Native;

internal static partial class Ole32
{
    internal const uint CoinitApartmentThreaded = 0x2;
    internal const uint CoinitDisableOle1Dde = 0x4;

    [LibraryImport("ole32.dll")]
    internal static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    internal static partial void CoUninitialize();
}
