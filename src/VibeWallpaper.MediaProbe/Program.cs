using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using LibVLCSharp.Shared;

return await ProbeProgram.RunAsync(args);

internal static class ProbeProgram
{
    private const int Success = 0;
    private const int Invalid = 2;
    private const int RuntimeUnavailable = 3;

    public static async Task<int> RunAsync(string[] args)
    {
        ProbeResponse response;
        int exitCode;
        var operation = "probe";
        var initializationCompleted = false;
        try
        {
            var request = ProbeRequest.Parse(args);
            operation = request.Operation;
            LibVLCSharp.Shared.Core.Initialize(request.RuntimePath);
            var libVlc = new LibVLC("--no-audio", "--no-video-title-show", "--no-osd");
            initializationCompleted = true;
            GC.SuppressFinalize(libVlc);
            response = request.Operation == "probe"
                ? await ProbeAsync(libVlc, request).ConfigureAwait(false)
                : await ThumbnailAsync(libVlc, request).ConfigureAwait(false);
            exitCode = response.Success ? Success : Invalid;
        }
        catch (VLCException exception)
        {
            response = ClassifyVlcException(operation, initializationCompleted, exception.Message);
            exitCode = initializationCompleted ? Invalid : RuntimeUnavailable;
        }
        catch (ArgumentException exception)
        {
            response = ProbeResponse.Error("video.helper.invalid_request", exception.Message);
            exitCode = Invalid;
        }
        catch (FileNotFoundException exception)
        {
            response = ProbeResponse.Error("video.source.missing", exception.Message);
            exitCode = Invalid;
        }

        await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response)).ConfigureAwait(false);
        await Console.Out.FlushAsync().ConfigureAwait(false);
        Environment.Exit(exitCode);
        return exitCode;
    }

    internal static ProbeResponse ClassifyVlcException(
        string operation,
        bool initializationCompleted,
        string message) =>
        !initializationCompleted
            ? ProbeResponse.Error("video.runtime.unavailable", message)
            : operation == "thumbnail"
                ? ProbeResponse.Error("video.thumbnail.failed", message)
                : ProbeResponse.Error("video.probe.invalid", message);

    private static async Task<ProbeResponse> ProbeAsync(LibVLC libVlc, ProbeRequest request)
    {
        using var media = new Media(libVlc, new Uri(request.SourcePath));
        var status = await media.Parse(MediaParseOptions.ParseLocal, 5_000)
            .ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        if (status != MediaParsedStatus.Done)
            return ProbeResponse.Error("video.probe.invalid", $"LibVLC parse status was {status}.");

        var videoTrack = media.Tracks.FirstOrDefault(static track => track.TrackType == TrackType.Video);
        if (videoTrack.TrackType != TrackType.Video || videoTrack.Data.Video.Width == 0 ||
            videoTrack.Data.Video.Height == 0 || media.Duration <= 0)
        {
            return ProbeResponse.Error(
                "video.probe.invalid",
                "Media has no playable, dimensioned video track with positive duration.");
        }

        var video = videoTrack.Data.Video;
        double? fps = video.FrameRateNum > 0 && video.FrameRateDen > 0
            ? (double)video.FrameRateNum / video.FrameRateDen
            : null;
        return new ProbeResponse(
            true,
            null,
            null,
            checked((int)video.Width),
            checked((int)video.Height),
            media.Duration,
            fps,
            FourCc(videoTrack.Codec),
            media.Tracks.Any(static track => track.TrackType == TrackType.Audio));
    }

    private static async Task<ProbeResponse> ThumbnailAsync(LibVLC libVlc, ProbeRequest request)
    {
        if (request.DestinationPath is null || request.CacheRootPath is null ||
            request.Width is null || request.Height is null)
            throw new ArgumentException("Thumbnail requires destination, width, and height.");

        using var media = new Media(libVlc, new Uri(request.SourcePath));
        using var player = new MediaPlayer(libVlc) { Media = media, Mute = true };
        var byteCount = checked((int)(request.Width.Value * request.Height.Value * 4));
        var allocation = Marshal.AllocHGlobal(byteCount + 31);
        var frameBuffer = new IntPtr((allocation.ToInt64() + 31) & ~31L);
        var decodedFrame = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        MediaPlayer.LibVLCVideoLockCb lockCallback = (_, planes) =>
        {
            Marshal.WriteIntPtr(planes, frameBuffer);
            return frameBuffer;
        };
        MediaPlayer.LibVLCVideoUnlockCb unlockCallback = (_, _, _) => { };
        MediaPlayer.LibVLCVideoDisplayCb displayCallback = (_, _) =>
        {
            var bytes = new byte[byteCount];
            Marshal.Copy(frameBuffer, bytes, 0, bytes.Length);
            decodedFrame.TrySetResult(bytes);
        };
        EventHandler<EventArgs> errorHandler = (_, _) => error.TrySetResult();
        player.SetVideoFormat("RV32", request.Width.Value, request.Height.Value, request.Width.Value * 4);
        player.SetVideoCallbacks(lockCallback, unlockCallback, displayCallback);
        player.EncounteredError += errorHandler;
        try
        {
            if (!player.Play()) return ProbeResponse.Error("video.thumbnail.playback_failed", "LibVLC rejected playback.");
            var completed = await Task.WhenAny(decodedFrame.Task, error.Task)
                .WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false);
            if (completed == error.Task)
                return ProbeResponse.Error("video.thumbnail.playback_failed", "LibVLC decode failed.");
            await PngWriter.WriteBgraAsync(
                request.DestinationPath,
                request.Width.Value,
                request.Height.Value,
                await decodedFrame.Task.ConfigureAwait(false)).ConfigureAwait(false);
            return ProbeResponse.ThumbnailSuccess();
        }
        catch (TimeoutException)
        {
            return ProbeResponse.Error("video.thumbnail.timeout", "Thumbnail operation exceeded six seconds.");
        }
        finally
        {
            player.EncounteredError -= errorHandler;
            player.Stop();
            Marshal.FreeHGlobal(allocation);
        }
    }

    private static string? FourCc(uint value)
    {
        if (value == 0) return null;
        Span<char> chars = stackalloc char[4];
        for (var index = 0; index < chars.Length; index++) chars[index] = (char)((value >> (8 * index)) & 0xFF);
        var result = new string(chars).TrimEnd('\0', ' ');
        return result.Length == 0 ? null : result;
    }
}

internal static class PngWriter
{
    private static readonly byte[] Signature = [137, 80, 78, 71, 13, 10, 26, 10];

    public static async Task WriteBgraAsync(string path, uint width, uint height, byte[] bgra)
    {
        var scanlines = new byte[checked((int)(height * (width * 4 + 1)))];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = checked((int)(y * width * 4));
            var destinationRow = checked((int)(y * (width * 4 + 1))) + 1;
            for (var x = 0; x < width; x++)
            {
                var source = sourceRow + checked((int)x * 4);
                var destination = destinationRow + checked((int)x * 4);
                scanlines[destination] = bgra[source + 2];
                scanlines[destination + 1] = bgra[source + 1];
                scanlines[destination + 2] = bgra[source];
                scanlines[destination + 3] = bgra[source + 3];
            }
        }

        byte[] compressed;
        await using (var compressedStream = new MemoryStream())
        {
            await using (var zlib = new ZLibStream(compressedStream, CompressionLevel.SmallestSize, leaveOpen: true))
                await zlib.WriteAsync(scanlines).ConfigureAwait(false);
            compressed = compressedStream.ToArray();
        }

        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true);
        await output.WriteAsync(Signature).ConfigureAwait(false);
        Span<byte> header = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(header, width);
        BinaryPrimitives.WriteUInt32BigEndian(header[4..], height);
        header[8] = 8;
        header[9] = 6;
        await WriteChunkAsync(output, "IHDR"u8.ToArray(), header.ToArray()).ConfigureAwait(false);
        await WriteChunkAsync(output, "IDAT"u8.ToArray(), compressed).ConfigureAwait(false);
        await WriteChunkAsync(output, "IEND"u8.ToArray(), Array.Empty<byte>()).ConfigureAwait(false);
    }

    private static async Task WriteChunkAsync(Stream output, ReadOnlyMemory<byte> type, ReadOnlyMemory<byte> data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)data.Length));
        await output.WriteAsync(length).ConfigureAwait(false);
        await output.WriteAsync(type).ConfigureAwait(false);
        await output.WriteAsync(data).ConfigureAwait(false);
        var crcInput = new byte[type.Length + data.Length];
        type.CopyTo(crcInput);
        data.CopyTo(crcInput.AsMemory(type.Length));
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(crcInput));
        await output.WriteAsync(length).ConfigureAwait(false);
    }

    private static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}

internal sealed record ProbeRequest(
    string Operation,
    string SourcePath,
    string RuntimePath,
    string? CacheRootPath,
    string? DestinationPath,
    uint? Width,
    uint? Height)
{
    public static ProbeRequest Parse(string[] args)
    {
        string? operation = null;
        string? source = null;
        string? runtime = null;
        string? cacheRoot = null;
        string? destination = null;
        uint? width = null;
        uint? height = null;
        for (var index = 0; index < args.Length; index += 2)
        {
            if (index + 1 >= args.Length) throw new ArgumentException("Every helper option requires a value.");
            switch (args[index])
            {
                case "--operation": operation = args[index + 1]; break;
                case "--source": source = args[index + 1]; break;
                case "--runtime": runtime = args[index + 1]; break;
                case "--cache-root": cacheRoot = args[index + 1]; break;
                case "--destination": destination = args[index + 1]; break;
                case "--width" when uint.TryParse(args[index + 1], out var parsedWidth): width = parsedWidth; break;
                case "--height" when uint.TryParse(args[index + 1], out var parsedHeight): height = parsedHeight; break;
                default: throw new ArgumentException($"Unknown or invalid helper option '{args[index]}'.");
            }
        }

        if (operation is not ("probe" or "thumbnail")) throw new ArgumentException("Operation must be probe or thumbnail.");
        if (string.IsNullOrWhiteSpace(source) || !Path.IsPathFullyQualified(source)) throw new ArgumentException("Absolute source required.");
        if (string.IsNullOrWhiteSpace(runtime) || !Path.IsPathFullyQualified(runtime)) throw new ArgumentException("Absolute runtime required.");
        source = Path.GetFullPath(source);
        runtime = Path.GetFullPath(runtime);
        if (!File.Exists(source)) throw new FileNotFoundException("Source does not exist.", source);
        if (operation == "thumbnail")
        {
            if (string.IsNullOrWhiteSpace(cacheRoot) || !Path.IsPathFullyQualified(cacheRoot))
                throw new ArgumentException("Thumbnail requires an absolute cache root.");
            if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination))
                throw new ArgumentException("Thumbnail requires an absolute destination.");
            if (width is null or 0 or > 512 || height is null or 0 or > 288)
                throw new ArgumentException("Thumbnail dimensions must be within 1..512 by 1..288.");
            cacheRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheRoot));
            destination = Path.GetFullPath(destination);
            if (!string.Equals(Path.GetDirectoryName(destination), cacheRoot, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Path.GetExtension(destination), ".png", StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileNameWithoutExtension(destination), "N", out _))
            {
                throw new ArgumentException("Thumbnail destination must be an expected PNG directly below the cache root.");
            }
        }
        else if (cacheRoot is not null || destination is not null || width is not null || height is not null)
        {
            throw new ArgumentException("Probe requests cannot include thumbnail output options.");
        }
        return new ProbeRequest(operation, source, runtime, cacheRoot, destination, width, height);
    }
}

internal sealed record ProbeResponse(
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
    public static ProbeResponse Error(string code, string message) =>
        new(false, code, message, null, null, null, null, null, null);
    public static ProbeResponse ThumbnailSuccess() =>
        new(true, null, null, null, null, null, null, null, null);
}
