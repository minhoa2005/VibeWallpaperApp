using System.Text.Json;
using VibeWallpaper.Engine.Diagnostics;

namespace VibeWallpaper.Tests.Diagnostics;

public sealed class RollingFileLogSinkTests
{
    [Fact]
    public async Task StructuredEvent_UsesAllowListedFieldsAndBoundedFiles()
    {
        var directory = Path.Combine(Path.GetTempPath(), "vibe-log-" + Guid.NewGuid().ToString("N"));
        await using var sink = new RollingFileLogSink(directory, maximumBytes: 200);
        await sink.WriteEventAsync(
            new DiagnosticEvent(
                "info",
                "engine",
                "A",
                "swap",
                12,
                "none",
                null,
                0,
                "Loading",
                "Running",
                RendererId: "renderer-1",
                OutputKey: "DISPLAY-1",
                Backend: "libvlc",
                PresentedFrames: 12,
                DroppedFrames: 1,
                RepeatedFrames: 0,
                LoopGeneration: 4,
                RecoveryCount: 1,
                HardwareDecodeConfirmed: true),
            TestContext.Current.CancellationToken);
        await sink.FlushAsync(TestContext.Current.CancellationToken);

        var files = Directory.GetFiles(directory, "vibe-wallpaper*.jsonl");
        Assert.NotEmpty(files);
        var json = await File.ReadAllTextAsync(files[0], TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(json);
        Assert.False(document.RootElement.TryGetProperty("message", out _));
        Assert.False(document.RootElement.TryGetProperty("payload", out _));
        Assert.False(document.RootElement.TryGetProperty("filePath", out _));
        Assert.Equal("swap", document.RootElement.GetProperty("operation").GetString());
        Assert.Equal("renderer-1", document.RootElement.GetProperty("rendererId").GetString());
        Assert.Equal("DISPLAY-1", document.RootElement.GetProperty("outputKey").GetString());
        Assert.Equal("libvlc", document.RootElement.GetProperty("backend").GetString());
        Assert.Equal(12, document.RootElement.GetProperty("presentedFrames").GetInt64());
        Directory.Delete(directory, recursive: true);
    }
}
