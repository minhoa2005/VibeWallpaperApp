using System.Globalization;

namespace VibeWallpaper.Tests.Native;

internal static class WindowsIntegrationGate
{
    internal const string EnvironmentVariable = "VIBE_WALLPAPER_RUN_WINDOWS_INTEGRATION";
    internal static string SkipReason =>
        $"Desktop-changing integration is disabled. Set {EnvironmentVariable}=1 to opt in explicitly.";

    internal static bool IsEnabled(string? value) => string.Equals(value, "1", StringComparison.Ordinal);
}

internal static class WindowsIntegrationPalette
{
    private const int MaximumRgbColors = 1 << 24;

    internal static IReadOnlyList<string> CreateDistinct(int count)
    {
        if (count < 0 || count > MaximumRgbColors)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        var colors = new string[count];
        for (var index = 0; index < count; index++)
        {
            var rgb = unchecked(0x203040u + ((uint)index * 0x9E3779u)) & 0x00FFFFFFu;
            colors[index] = $"#{rgb.ToString("X6", CultureInfo.InvariantCulture)}";
        }

        return colors;
    }
}
