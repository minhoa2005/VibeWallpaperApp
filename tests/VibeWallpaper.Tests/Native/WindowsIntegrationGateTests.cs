namespace VibeWallpaper.Tests.Native;

public sealed class WindowsIntegrationGateTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("1", true)]
    public void IsEnabled_RequiresExplicitOne(string? value, bool expected)
    {
        Assert.Equal(expected, WindowsIntegrationGate.IsEnabled(value));
    }

    [Fact]
    public void CreateDistinct_ProducesUniqueColorsBeyondTheInitialFourOutputs()
    {
        var colors = WindowsIntegrationPalette.CreateDistinct(256);

        Assert.Equal(256, colors.Count);
        Assert.Equal(256, colors.Distinct(StringComparer.Ordinal).Count());
        Assert.All(colors, color => Assert.Matches("^#[0-9A-F]{6}$", color));
    }
}
