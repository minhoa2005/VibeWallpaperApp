using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Monitors;

namespace VibeWallpaper.Tests.Monitors;

public sealed class DisplayIdentityMatcherTests
{
    [Fact]
    public void Match_WhenCoordinatesAndFriendlyNameChange_UsesMonitorDevicePath()
    {
        var persisted = PersistedReference(path: "DISPLAY#ACME123#A", name: "Old name", bounds: Rect(0, 0, 1920, 1080));
        var current = Output("current", path: "DISPLAY#ACME123#A", name: "New name", bounds: Rect(1920, 0, 2560, 1440));

        var match = DisplayIdentityMatcher.Match(persisted, [current]);

        Assert.Equal(current.Descriptor.Identity, match!.Descriptor.Identity);
    }

    [Fact]
    public void Match_WhenDevicePathDiffers_UsesUniqueEdidIdentity()
    {
        var persisted = PersistedReference(path: "DISPLAY#OLD", manufacturer: "ACM", product: 42, serial: 1234);
        var expected = Output("replacement", path: "DISPLAY#NEW", manufacturer: "ACM", product: 42, serial: 1234);
        var other = Output("other", path: "DISPLAY#OTHER", manufacturer: "XYZ", product: 9, serial: 8);

        var match = DisplayIdentityMatcher.Match(persisted, [other, expected]);

        Assert.Equal(expected.Descriptor.Identity, match!.Descriptor.Identity);
    }

    [Fact]
    public void Match_WhenEdidIsUnavailable_UsesUniqueCompatibleTargetAndConnector()
    {
        var persisted = PersistedReference(path: null, adapter: 88, target: 3, connector: 11);
        var expected = Output("target", path: null, adapter: 88, target: 3, connector: 11);
        var other = Output("other", path: null, adapter: 88, target: 4, connector: 12);

        var match = DisplayIdentityMatcher.Match(persisted, [other, expected]);

        Assert.Equal(expected.Descriptor.Identity, match!.Descriptor.Identity);
    }

    [Fact]
    public void Match_WhenOnlyPreviousTopologyEvidenceExists_UsesUniqueSimilarity()
    {
        var bounds = Rect(-1920, 0, 1920, 1080);
        var persisted = PersistedReference(path: null, name: "Studio", bounds: bounds);
        var expected = Output("similar", path: null, name: "Studio", bounds: bounds);
        var other = Output("other", path: null, name: "Projector", bounds: Rect(0, 0, 1280, 720));

        var match = DisplayIdentityMatcher.Match(persisted, [other, expected]);

        Assert.Equal(expected.Descriptor.Identity, match!.Descriptor.Identity);
    }

    [Fact]
    public void Match_WhenBoundsChange_UsesFriendlyNameOnlyWhenItIsUnique()
    {
        var persisted = PersistedReference(path: null, name: "Studio", bounds: Rect(0, 0, 1920, 1080));
        var expected = Output("named", path: null, name: "Studio", bounds: Rect(1920, 0, 2560, 1440));
        var other = Output("other", path: null, name: "Projector", bounds: Rect(0, 0, 1280, 720));

        Assert.Equal(expected, DisplayIdentityMatcher.Match(persisted, [other, expected]));
    }

    [Fact]
    public void Match_WhenFriendlyNameChanges_UsesBoundsOnlyWhenTheyAreUnique()
    {
        var bounds = Rect(-1920, 0, 1920, 1080);
        var persisted = PersistedReference(path: null, name: "Old name", bounds: bounds);
        var expected = Output("positioned", path: null, name: "New name", bounds: bounds);
        var other = Output("other", path: null, name: "Projector", bounds: Rect(0, 0, 1920, 1080));

        Assert.Equal(expected, DisplayIdentityMatcher.Match(persisted, [other, expected]));
    }

    [Fact]
    public void Match_WhenWeakEvidenceIsAmbiguous_ReturnsUnmatchedInsteadOfGuessing()
    {
        var bounds = Rect(0, 0, 1920, 1080);
        var persisted = PersistedReference(path: null, name: "Generic PnP Monitor", bounds: bounds);
        var first = Output("first", path: null, name: "Generic PnP Monitor", bounds: bounds);
        var second = Output("second", path: null, name: "Generic PnP Monitor", bounds: bounds);

        Assert.Null(DisplayIdentityMatcher.Match(persisted, [first, second]));
    }

    [Fact]
    public void Match_WhenEdidEvidenceIsAmbiguous_DoesNotFallBackAndGuess()
    {
        var persisted = PersistedReference(path: null, manufacturer: "ACM", product: 42, serial: 1234, name: "Unique old name");
        var first = Output("first", path: null, manufacturer: "ACM", product: 42, serial: 1234, name: "Unique old name");
        var second = Output("second", path: null, manufacturer: "ACM", product: 42, serial: 1234, name: "Other");

        Assert.Null(DisplayIdentityMatcher.Match(persisted, [first, second]));
    }

    [Fact]
    public void MatchDetailed_ReportsTheWinningEvidenceAndConfidence()
    {
        var persisted = PersistedReference(path: "DISPLAY#ACME123#A");
        var current = Output("current", path: "DISPLAY#ACME123#A");

        var match = DisplayIdentityMatcher.MatchDetailed(persisted, [current]);

        Assert.NotNull(match);
        Assert.Equal(DisplayIdentityConfidence.ExactDevicePath, match.Confidence);
        Assert.Equal(DisplayIdentityEvidenceKind.MonitorDevicePath, match.Evidence);
        Assert.Same(current, match.Output);
    }

    private static PersistedMonitorReference PersistedReference(
        string? path,
        long adapter = 0,
        uint target = 0,
        uint? connector = null,
        string? manufacturer = null,
        ushort? product = null,
        uint? serial = null,
        string name = "Monitor",
        DisplayViewport? bounds = null) =>
        new(
            new MonitorIdentity("persisted"),
            Evidence(path, adapter, target, connector, manufacturer, product, serial, name, bounds ?? Rect(0, 0, 1920, 1080)));

    private static DisplayTopologyOutput Output(
        string key,
        string? path,
        long adapter = 0,
        uint target = 0,
        uint? connector = null,
        string? manufacturer = null,
        ushort? product = null,
        uint? serial = null,
        string name = "Monitor",
        DisplayViewport? bounds = null)
    {
        var viewport = bounds ?? Rect(0, 0, 1920, 1080);
        var evidence = Evidence(path, adapter, target, connector, manufacturer, product, serial, name, viewport);
        var descriptor = new MonitorDescriptor(
            new MonitorIdentity(key), evidence, name, viewport, viewport, 96, 1.0,
            DisplayOrientation.Landscape, false);
        return new DisplayTopologyOutput(descriptor, $"clone:{key}", [evidence]);
    }

    private static MonitorIdentityEvidence Evidence(
        string? path,
        long adapter,
        uint target,
        uint? connector,
        string? manufacturer,
        ushort? product,
        uint? serial,
        string name,
        DisplayViewport bounds) =>
        new(adapter, 1, target, connector, null, path, manufacturer, product, serial, name, bounds);

    private static DisplayViewport Rect(int x, int y, int width, int height) => new(x, y, width, height);
}
