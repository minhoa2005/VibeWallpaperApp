using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.Engine.Import.Video;

public sealed class LibVlcRuntimeUnavailableException : Exception
{
    public LibVlcRuntimeUnavailableException(string message) : base(message) { }
    public LibVlcRuntimeUnavailableException(string message, Exception innerException) : base(message, innerException) { }
}

public sealed class VideoProbeService : IVideoProbeService
{
    internal static TimeSpan DefaultTimeout { get; } = TimeSpan.FromSeconds(30);
    private readonly LibVlcProcessClient _client;

    public VideoProbeService(string? helperPath = null, TimeSpan? timeout = null) =>
        _client = new LibVlcProcessClient(helperPath, timeout);

    public async Task<VideoMetadata> ProbeAsync(string absolutePath, CancellationToken cancellationToken)
    {
        var result = await _client.RunAsync(
            "probe", absolutePath, null, null, null, null, cancellationToken).ConfigureAwait(false);
        if (!result.Success || result.Width is null || result.Height is null || result.DurationMilliseconds is null)
            throw new VideoProbeException(result.DiagnosticCode ?? "video.probe.invalid", result.Message ?? "Video probe failed.");
        return new VideoMetadata(
            result.Width.Value,
            result.Height.Value,
            TimeSpan.FromMilliseconds(result.DurationMilliseconds.Value),
            result.NominalFps,
            result.VideoCodec,
            result.HasAudio == true);
    }
}

internal sealed class LibVlcProcessClient
{
    private readonly string _helperPath;
    private readonly string _runtimePath;
    private readonly TimeSpan _timeout;
    private readonly Func<ProcessStartInfo>? _startInfoFactory;
    private readonly Action<int>? _processStarted;

    public LibVlcProcessClient(
        string? helperPath = null,
        TimeSpan? timeout = null,
        Func<ProcessStartInfo>? startInfoFactory = null,
        Action<int>? processStarted = null)
    {
        _helperPath = Path.GetFullPath(helperPath ?? Path.Combine(AppContext.BaseDirectory, "VibeWallpaper.MediaProbe.exe"));
        _runtimePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "libvlc", RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "win-x64" : "unsupported"));
        _timeout = timeout ?? VideoProbeService.DefaultTimeout;
        _startInfoFactory = startInfoFactory;
        _processStarted = processStarted;
        if (!File.Exists(_helperPath))
            throw new LibVlcRuntimeUnavailableException($"LibVLC helper executable is missing at '{_helperPath}'.");
        if (string.Equals(
                Path.GetFileName(_helperPath),
                "VibeWallpaper.MediaProbe.exe",
                StringComparison.OrdinalIgnoreCase) &&
            !File.Exists(Path.ChangeExtension(_helperPath, ".dll")))
        {
            throw new LibVlcRuntimeUnavailableException(
                $"LibVLC helper assembly is missing at '{Path.ChangeExtension(_helperPath, ".dll")}'.");
        }
        if (!Environment.Is64BitProcess || !File.Exists(Path.Combine(_runtimePath, "libvlc.dll")) ||
            !File.Exists(Path.Combine(_runtimePath, "libvlccore.dll")) || !Directory.Exists(Path.Combine(_runtimePath, "plugins")))
        {
            throw new LibVlcRuntimeUnavailableException(
                $"Expected pinned x64 native files at '{_runtimePath}' (libvlc.dll, libvlccore.dll, plugins).");
        }
    }

    public async Task<ProbeProcessResponse> RunAsync(
        string operation,
        string sourcePath,
        string? destinationPath,
        uint? width,
        uint? height,
        string? cacheRootPath,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || !Path.IsPathFullyQualified(sourcePath))
            throw new ArgumentException("An absolute source path is required.", nameof(sourcePath));
        var startInfo = _startInfoFactory?.Invoke() ?? new ProcessStartInfo
        {
            FileName = _helperPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        Add(startInfo, "--operation", operation);
        Add(startInfo, "--source", Path.GetFullPath(sourcePath));
        Add(startInfo, "--runtime", _runtimePath);
        if (cacheRootPath is not null) Add(startInfo, "--cache-root", Path.GetFullPath(cacheRootPath));
        if (destinationPath is not null) Add(startInfo, "--destination", Path.GetFullPath(destinationPath));
        if (width is not null) Add(startInfo, "--width", width.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (height is not null) Add(startInfo, "--height", height.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start()) throw new VideoProbeException("video.helper.start_failed", "Media helper did not start.");
        _processStarted?.Invoke(process.Id);
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_timeout);
        try
        {
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var cleanupCompleted = await KillExactAndWaitAsync(process, outputTask, errorTask).ConfigureAwait(false);
            throw new VideoProbeException(
                "video.helper.timeout",
                $"Media helper exceeded {_timeout.TotalSeconds:0.#} seconds; exact-child cleanup " +
                (cleanupCompleted ? "completed." : "did not complete within two seconds."));
        }
        catch (OperationCanceledException)
        {
            var cleanupCompleted = await KillExactAndWaitAsync(process, outputTask, errorTask).ConfigureAwait(false);
            throw new OperationCanceledException(
                "Media helper was canceled; exact-child cleanup " +
                (cleanupCompleted ? "completed." : "did not complete within two seconds."),
                cancellationToken);
        }

        var output = await outputTask.ConfigureAwait(false);
        _ = await errorTask.ConfigureAwait(false);
        var response = DeserializeLastLine(output);
        if (process.ExitCode == 3)
            throw new LibVlcRuntimeUnavailableException(response.Message ?? "Pinned LibVLC runtime could not initialize in helper.");
        if (process.ExitCode != 0 && response.DiagnosticCode is null)
            throw new VideoProbeException("video.helper.crashed", $"Media helper exited with code {process.ExitCode}.");
        return response;
    }

    private static ProbeProcessResponse DeserializeLastLine(string output)
    {
        var line = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (line is null)
            return ProbeProcessResponse.Error("video.helper.crashed", "Media helper emitted no structured response.");
        try
        {
            return JsonSerializer.Deserialize<ProbeProcessResponse>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? ProbeProcessResponse.Error("video.helper.invalid_response", "Media helper emitted an empty response.");
        }
        catch (JsonException exception)
        {
            throw new VideoProbeException("video.helper.invalid_response", "Media helper emitted invalid JSON.", exception);
        }
    }

    private static async Task<bool> KillExactAndWaitAsync(
        Process process,
        Task<string> outputTask,
        Task<string> errorTask)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: false);
        }
        catch (InvalidOperationException)
        {
            // The exact child won the race and exited before Kill.
        }
        catch (System.ComponentModel.Win32Exception)
        {
            if (!process.HasExited) return false;
        }

        try
        {
            await Task.WhenAll(
                    process.WaitForExitAsync(),
                    ObservePipeCompletionAsync(outputTask),
                    ObservePipeCompletionAsync(errorTask))
                .WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return process.HasExited;
        }
        catch (TimeoutException) { return false; }
    }

    private static async Task ObservePipeCompletionAsync(Task<string> pipeTask)
    {
        try { _ = await pipeTask.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private static void Add(ProcessStartInfo info, string name, string value)
    {
        info.ArgumentList.Add(name);
        info.ArgumentList.Add(value);
    }
}

internal sealed record ProbeProcessResponse(
    bool Success,
    string? DiagnosticCode,
    string? Message,
    int? Width,
    int? Height,
    long? DurationMilliseconds,
    double? NominalFps,
    string? VideoCodec,
    bool? HasAudio)
{
    public static ProbeProcessResponse Error(string code, string message) =>
        new(false, code, message, null, null, null, null, null, null);
}
