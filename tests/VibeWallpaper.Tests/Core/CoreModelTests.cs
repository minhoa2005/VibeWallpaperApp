using System.Text.Json;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Tests.Core;

public sealed class CoreModelTests
{
    [Fact]
    public void DisplayViewport_RejectsNonPositiveSize()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayViewport(0, 0, 0, 1080));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DisplayViewport(0, 0, 1920, 0));
    }

    [Fact]
    public void DisplayViewport_PreservesNegativeVirtualDesktopOrigin()
    {
        var viewport = new DisplayViewport(-1920, -1080, 1920, 1080);

        Assert.Equal(-1920, viewport.X);
        Assert.Equal(-1080, viewport.Y);
    }

    [Fact]
    public void WebSource_RequiresAbsoluteDirectory()
    {
        Assert.Throws<ArgumentException>(() => WebSource.Create("relative", "index.html"));
    }

    [Fact]
    public void WallpaperId_RejectsEmptyGuid()
    {
        Assert.Throws<ArgumentException>(() => new WallpaperId(Guid.Empty));
    }

    [Fact]
    public void MonitorIdentity_RejectsBlankKey()
    {
        Assert.Throws<ArgumentException>(() => new MonitorIdentity(" "));
    }

    [Fact]
    public void IdFactories_CreateNonEmptyIds()
    {
        Assert.NotEqual(Guid.Empty, WallpaperId.New().Value);
        Assert.NotEqual(Guid.Empty, RendererInstanceId.New().Value);
    }

    [Fact]
    public void MonitorIdentity_TrimsKey()
    {
        Assert.Equal("DISPLAY-A", new MonitorIdentity("  DISPLAY-A  ").Key);
    }

    [Fact]
    public void VideoSource_CanonicalizesAbsolutePath_WithoutRequiringExistence()
    {
        var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "vibe", "media", "..", "clip.mp4"));
        var source = VideoSource.Create(Path.Combine(Path.GetTempPath(), "vibe", "media", "..", "clip.mp4"));

        Assert.Equal(expected, source.FilePath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.mp4")]
    public void VideoSource_RejectsEmptyOrRelativePath(string path)
    {
        Assert.Throws<ArgumentException>(() => VideoSource.Create(path));
    }

    [Fact]
    public void VideoSource_RejectsDirectoryPath()
    {
        Assert.Throws<ArgumentException>(() => VideoSource.Create(Path.GetTempPath()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("C:\\absolute.html")]
    public void WebSource_RejectsEmptyOrRootedEntryPoint(string entryPoint)
    {
        Assert.Throws<ArgumentException>(() => WebSource.Create(Path.GetTempPath(), entryPoint));
    }

    [Fact]
    public void WebSource_RejectsRootedButNotFullyQualifiedEntryPoints()
    {
        var currentDriveRoot = Path.GetPathRoot(Environment.CurrentDirectory)!;
        var driveRelativeEntry = $"{currentDriveRoot[0]}:entry.html";

        Assert.Throws<ArgumentException>(() => WebSource.Create(currentDriveRoot, "\\entry.html"));
        Assert.Throws<ArgumentException>(() => WebSource.Create(currentDriveRoot, driveRelativeEntry));
    }

    [Theory]
    [InlineData("..\\escape.html")]
    [InlineData(".")]
    public void WebSource_RejectsRootAndTraversalEntries(string entryPoint)
    {
        Assert.Throws<ArgumentException>(() => WebSource.Create(Path.Combine(Path.GetTempPath(), "vibe-root"), entryPoint));
    }

    [Fact]
    public void WebSource_RejectsSiblingWithSharedTextualPrefix()
    {
        var parent = Path.Combine(Path.GetTempPath(), "vibe-wallpaper-tests");
        var root = Path.Combine(parent, "wallpaper");

        Assert.Throws<ArgumentException>(() => WebSource.Create(root, "..\\wallpaper-other\\index.html"));
    }

    [Fact]
    public void WebSource_CanonicalizesRootAndNormalizesRelativeEntryPoint()
    {
        var root = Path.Combine(Path.GetTempPath(), "vibe-web", "content", "..");
        var source = WebSource.Create(root, "assets\\..\\index.html");

        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)), source.DirectoryPath);
        Assert.Equal("index.html", source.EntryPoint);
    }

    [Theory]
    [InlineData("#00aaFF", "#00AAFF")]
    [InlineData("#000000", "#000000")]
    public void SolidColorSource_NormalizesColor(string input, string expected)
    {
        Assert.Equal(expected, SolidColorSource.Create(input).HexColor);
    }

    [Theory]
    [InlineData("00AAFF")]
    [InlineData("#0AF")]
    [InlineData("#00AAFG")]
    [InlineData("#00AAFF00")]
    public void SolidColorSource_RejectsNonRgbHex(string color)
    {
        Assert.Throws<ArgumentException>(() => SolidColorSource.Create(color));
    }

    [Fact]
    public void WallpaperDefinition_AcceptsValidKindSpecificSettings()
    {
        var video = CreateDefinition(VideoSource.Create(AbsoluteFile("clip.mp4")), networkEnabled: false, audioEnabled: true, volumePercent: 80, interactionEnabled: false);
        var web = CreateDefinition(WebSource.Create(AbsoluteDirectory("web"), "index.html"), networkEnabled: true, audioEnabled: false, volumePercent: 0, interactionEnabled: true);
        var solid = CreateDefinition(SolidColorSource.Create("#112233"), networkEnabled: false, audioEnabled: false, volumePercent: 0, interactionEnabled: false);

        Assert.Equal("Example", video.Name);
        Assert.True(web.NetworkEnabled);
        Assert.Equal(WallpaperKind.SolidColor, solid.Source.Kind);
    }

    [Fact]
    public void WallpaperDefinition_RejectsInvalidSettingsAndEnums()
    {
        var source = VideoSource.Create(AbsoluteFile("clip.mp4"));

        Assert.Throws<ArgumentException>(() => new WallpaperDefinition(new WallpaperId(Guid.NewGuid()), "Example", source, (FitMode)999, 30, false, false, 0, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDefinition(source, targetFps: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateDefinition(source, volumePercent: 101));
        Assert.Throws<ArgumentException>(() => CreateDefinition(source, networkEnabled: true));
        Assert.Throws<ArgumentException>(() => CreateDefinition(SolidColorSource.Create("#112233"), audioEnabled: true));
        Assert.Throws<ArgumentException>(() => CreateDefinition(SolidColorSource.Create("#112233"), volumePercent: 1));
    }

    [Fact]
    public void WallpaperDefinition_RejectsNullSourceAndBlankName()
    {
        Assert.Throws<ArgumentNullException>(() => new WallpaperDefinition(new WallpaperId(Guid.NewGuid()), "Example", null!, FitMode.Cover, 30, false, false, 0, false));
        Assert.Throws<ArgumentException>(() => CreateDefinition(VideoSource.Create(AbsoluteFile("clip.mp4")), name: "  "));
    }

    [Fact]
    public void MonitorDescriptor_ValidatesGeometryDpiScaleAndOrientation()
    {
        var identity = new MonitorIdentity("display-1");
        var evidence = CreateEvidence();
        var bounds = new DisplayViewport(0, 0, 1920, 1080);

        Assert.Throws<ArgumentNullException>(() => new MonitorDescriptor(null!, evidence, "Monitor", bounds, bounds, 96, 1, DisplayOrientation.Landscape, true));
        Assert.Throws<ArgumentNullException>(() => new MonitorDescriptor(identity, null!, "Monitor", bounds, bounds, 96, 1, DisplayOrientation.Landscape, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonitorDescriptor(identity, evidence, "Monitor", bounds, bounds, 95, 1, DisplayOrientation.Landscape, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => new MonitorDescriptor(identity, evidence, "Monitor", bounds, bounds, 96, double.NaN, DisplayOrientation.Landscape, true));
        Assert.Throws<ArgumentException>(() => new MonitorDescriptor(identity, evidence, "Monitor", bounds, bounds, 96, 1, (DisplayOrientation)99, true));
    }

    [Fact]
    public void MonitorDescriptor_TrimsNameAndPreservesPrimaryFlag()
    {
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        var descriptor = new MonitorDescriptor(new MonitorIdentity("display-1"), CreateEvidence(), "  Desk  ", bounds, bounds, 96, 1, DisplayOrientation.Landscape, true);

        Assert.Equal("Desk", descriptor.FriendlyName);
        Assert.True(descriptor.IsPrimary);
    }

    [Fact]
    public void RendererContext_RejectsZeroHandleAndNullValues()
    {
        var monitor = CreateMonitor();
        var viewport = new DisplayViewport(0, 0, 1920, 1080);

        Assert.Throws<ArgumentOutOfRangeException>(() => new RendererContext(0, monitor, viewport, viewport));
        Assert.Throws<ArgumentNullException>(() => new RendererContext(1, null!, viewport, viewport));
        Assert.Throws<ArgumentNullException>(() => new RendererContext(1, monitor, null!, viewport));
        Assert.Throws<ArgumentNullException>(() => new RendererContext(1, monitor, viewport, null!));
    }

    [Fact]
    public void RendererContext_PreservesBorrowedHandleAndGeometry()
    {
        var monitor = CreateMonitor();
        var canvas = new DisplayViewport(-1920, 0, 3840, 1080);
        var viewport = new DisplayViewport(-1920, 0, 1920, 1080);

        var context = new RendererContext(42, monitor, canvas, viewport);

        Assert.Equal((nint)42, context.HostHwnd);
        Assert.Same(monitor, context.Monitor);
        Assert.Same(canvas, context.VirtualCanvas);
    }

    [Fact]
    public void RendererStateMachine_EnforcesLifecycleAndKeepsPerformanceIndependent()
    {
        var state = new RendererStateMachine();

        Assert.Throws<InvalidOperationException>(() => state.TransitionTo(RendererLifecycle.Loading));
        state.TransitionTo(RendererLifecycle.Initializing);
        state.TransitionTo(RendererLifecycle.Loading);
        state.TransitionTo(RendererLifecycle.Ready);
        state.SetPerformanceState(PerformanceState.Suspended);

        Assert.Equal(RendererLifecycle.Ready, state.Lifecycle);
        Assert.Equal(PerformanceState.Suspended, state.PerformanceState);
        Assert.Throws<InvalidOperationException>(() => state.TransitionTo(RendererLifecycle.Initializing));
    }

    [Fact]
    public void RendererStateMachine_AllowsFaultStopAndDisposeWithIdempotentTerminals()
    {
        var state = new RendererStateMachine();

        state.TransitionTo(RendererLifecycle.Faulted);
        state.Stop();
        state.Stop();
        Assert.Equal(RendererLifecycle.Stopped, state.Lifecycle);
        state.Dispose();
        state.Dispose();

        Assert.Equal(RendererLifecycle.Disposed, state.Lifecycle);
        state.Stop();
        Assert.Equal(RendererLifecycle.Disposed, state.Lifecycle);
        Assert.Throws<InvalidOperationException>(() => state.SetPerformanceState(PerformanceState.Running));
        Assert.Throws<InvalidOperationException>(() => state.TransitionTo(RendererLifecycle.Active));
    }

    [Fact]
    public void WallpaperSources_RoundTripThroughBaseType()
    {
        WallpaperSource[] sources =
        [
            VideoSource.Create(AbsoluteFile("clip.mp4")),
            WebSource.Create(AbsoluteDirectory("web"), "assets/index.html"),
            SolidColorSource.Create("#aabbcc"),
        ];

        foreach (var source in sources)
        {
            var json = JsonSerializer.Serialize<WallpaperSource>(source);
            var roundTripped = JsonSerializer.Deserialize<WallpaperSource>(json);

            Assert.NotNull(roundTripped);
            Assert.Equal(source, roundTripped);
        }
    }

    private static WallpaperDefinition CreateDefinition(
        WallpaperSource source,
        string name = "Example",
        int targetFps = 30,
        bool networkEnabled = false,
        bool audioEnabled = false,
        int volumePercent = 0,
        bool interactionEnabled = false) =>
        new(new WallpaperId(Guid.NewGuid()), name, source, FitMode.Cover, targetFps, networkEnabled, audioEnabled, volumePercent, interactionEnabled);

    private static MonitorIdentityEvidence CreateEvidence() =>
        new(1, 2, 3, 4, "target", "device", "VIB", 5, 6, "Monitor", new DisplayViewport(0, 0, 1920, 1080));

    private static MonitorDescriptor CreateMonitor()
    {
        var bounds = new DisplayViewport(0, 0, 1920, 1080);
        return new MonitorDescriptor(new MonitorIdentity("display-1"), CreateEvidence(), "Monitor", bounds, bounds, 96, 1, DisplayOrientation.Landscape, true);
    }

    private static string AbsoluteDirectory(string name) => Path.Combine(Path.GetTempPath(), "vibe-wallpaper-tests", name);

    private static string AbsoluteFile(string name) => Path.Combine(Path.GetTempPath(), "vibe-wallpaper-tests", name);
}
