using System.Diagnostics;
using VibeWallpaper.Engine.Import.Video;

namespace VibeWallpaper.Tests.Rendering;

public sealed class MediaProbeProtocolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vibe-helper-protocol-{Guid.NewGuid():N}");

    public MediaProbeProtocolTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public void Parse_RejectsZeroOrOversizedThumbnailDimensionsAndOutsideCacheDestination()
    {
        var source = CreateSource();
        var runtime = Path.Combine(_directory, "runtime");
        var cache = Path.Combine(_directory, "cache", "thumbnails");
        Directory.CreateDirectory(runtime);
        Directory.CreateDirectory(cache);

        Assert.Throws<ArgumentException>(() => ProbeRequest.Parse(ThumbnailArgs(source, runtime, cache, Path.Combine(cache, "x.png"), 0, 10)));
        Assert.Throws<ArgumentException>(() => ProbeRequest.Parse(ThumbnailArgs(source, runtime, cache, Path.Combine(cache, "x.png"), 513, 10)));
        Assert.Throws<ArgumentException>(() => ProbeRequest.Parse(ThumbnailArgs(source, runtime, cache, Path.Combine(_directory, "outside.png"), 10, 10)));
        Assert.Throws<ArgumentException>(() => ProbeRequest.Parse(ThumbnailArgs(source, runtime, cache, Path.Combine(cache, "..", "escape.png"), 10, 10)));
    }

    [Fact]
    public void ClassifyVlcException_OnlyInitializationIsRuntimeUnavailable()
    {
        var initialization = ProbeProgram.ClassifyVlcException("probe", initializationCompleted: false, "init failed");
        var probe = ProbeProgram.ClassifyVlcException("probe", initializationCompleted: true, "parse failed");
        var thumbnail = ProbeProgram.ClassifyVlcException("thumbnail", initializationCompleted: true, "decode failed");

        Assert.Equal("video.runtime.unavailable", initialization.DiagnosticCode);
        Assert.Equal("video.probe.invalid", probe.DiagnosticCode);
        Assert.Equal("video.thumbnail.failed", thumbnail.DiagnosticCode);
    }

    [Fact]
    public async Task Timeout_KillsExactChildAndAwaitsExitBeforeReturningDiagnostic()
    {
        var source = CreateSource();
        var powershell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        var childId = 0;
        var client = new LibVlcProcessClient(
            helperPath: powershell,
            timeout: TimeSpan.FromMilliseconds(100),
            startInfoFactory: () =>
            {
                var info = new ProcessStartInfo(powershell)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add("$e=[Threading.ManualResetEvent]::new($false);$e.WaitOne()");
                return info;
            },
            processStarted: id => childId = id);

        var error = await Assert.ThrowsAsync<VideoProbeException>(
            () => client.RunAsync("probe", source, null, null, null, null, TestContext.Current.CancellationToken));

        Assert.Equal("video.helper.timeout", error.DiagnosticCode);
        Assert.Contains("cleanup completed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(0, childId);
        Assert.Throws<ArgumentException>(() => Process.GetProcessById(childId));
    }

    [Fact]
    public void Constructor_ManagedHelperWithoutAssemblyReportsRuntimeUnavailableBeforeLaunch()
    {
        var helper = Path.Combine(_directory, "VibeWallpaper.MediaProbe.exe");
        File.WriteAllBytes(helper, [1]);
        var runtime = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        Assert.True(File.Exists(Path.Combine(runtime, "libvlc.dll")));

        var error = Assert.Throws<LibVlcRuntimeUnavailableException>(() =>
            new LibVlcProcessClient(helperPath: helper));

        Assert.Contains("VibeWallpaper.MediaProbe.dll", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string CreateSource()
    {
        var path = Path.Combine(_directory, "source.mp4");
        File.WriteAllBytes(path, [1]);
        return path;
    }

    private static string[] ThumbnailArgs(
        string source, string runtime, string cache, string destination, uint width, uint height) =>
    [
        "--operation", "thumbnail",
        "--source", source,
        "--runtime", runtime,
        "--cache-root", cache,
        "--destination", destination,
        "--width", width.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "--height", height.ToString(System.Globalization.CultureInfo.InvariantCulture),
    ];
}
