using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Video;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Rendering;

public sealed class VideoRendererIntegrationTests : IDisposable
{
    // LibVLC/D3D11 retains process-wide decoder, plugin, and GPU-device state after warmup.
    // The first batch may establish that cache within +32 handles. A same-size second batch
    // must plateau within +8 handles instead of permitting one leaked handle per cycle.
    private const int HandleTolerance = 32;
    private const int PlateauTolerance = 8;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"vibe-renderer-{Guid.NewGuid():N}");

    public VideoRendererIntegrationTests() => Directory.CreateDirectory(_directory);

    [Fact]
    [Trait("Category", "LibVLCIntegration")]
    public async Task HiddenHwnd_PlayMutePauseSeekResumeAndDispose_ReleasesOwnedOutput()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        await using var runtime = CreateRuntimeOrSkip();
        var capturing = new CapturingRuntime(runtime);
        var source = TinyGifTestAsset.Create(_directory, "loop.gif");
        var renderer = new VideoRenderer(
            dispatcher, capturing, new FakeVideoProbeService(), VideoSurfaceWindowFactory.Instance, VideoRendererOptions.Default);
        var host = await CreateHostAsync(dispatcher);
        try
        {
            await ActivateAsync(dispatcher, renderer, host, source);
            var player = Assert.Single(capturing.Players);

            await WaitUntilAsync(async () => await OnEngineAsync(dispatcher, () => player.IsPlaying), TimeSpan.FromSeconds(3));
            await WaitUntilAsync(async () => await OnEngineAsync(dispatcher, () => player.TimeMilliseconds > 80), TimeSpan.FromSeconds(3));
            Assert.True(await OnEngineAsync(dispatcher, () => player.IsMuted));

            await dispatcher.InvokeAsync(
                token => new ValueTask(renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Suspended), token)),
                TestContext.Current.CancellationToken);
            var pausedAt = await OnEngineAsync(dispatcher, () => player.TimeMilliseconds);
            await WaitForDurationAsync(TimeSpan.FromMilliseconds(250));
            var pausedLater = await OnEngineAsync(dispatcher, () => player.TimeMilliseconds);
            Assert.InRange(Math.Abs(pausedLater - pausedAt), 0, 100);

            await dispatcher.InvokeAsync(async token =>
            {
                player.TimeMilliseconds = 500;
                await renderer.ApplyPerformanceAsync(new RendererPerformanceRequest(PerformanceState.Running), token);
            }, TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                async () => await OnEngineAsync(dispatcher, () => player.IsPlaying && player.TimeMilliseconds >= 450),
                TimeSpan.FromSeconds(3));

            var child = player.AssignedHwnd;
            Assert.NotEqual(0, child);
            await renderer.DisposeAsync();
            Assert.False(NativeMethods.IsWindow(child));
            Assert.True(NativeMethods.IsWindow(host));
        }
        finally
        {
            await renderer.DisposeAsync();
            await DestroyHostAsync(dispatcher, host);
        }
    }

    [Fact]
    [Trait("Category", "LibVLCIntegration")]
    public async Task NativeRepeat_AdvancesAcrossThreeWrapsWithoutReplayCommand()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        await using var runtime = CreateRuntimeOrSkip();
        var capturing = new CapturingRuntime(runtime);
        var source = NativeRepeatProgressTestAsset.Create(_directory, "native-repeat.mp4");
        var renderer = new VideoRenderer(
            dispatcher, capturing, new FakeVideoProbeService(), VideoSurfaceWindowFactory.Instance, VideoRendererOptions.Default);
        var host = await CreateHostAsync(dispatcher);
        try
        {
            await ActivateAsync(dispatcher, renderer, host, source);
            var player = Assert.Single(capturing.Players);
            await WaitUntilAsync(async () => await OnEngineAsync(dispatcher, () => player.ObservedWraps >= 3),
                TimeSpan.FromSeconds(25),
                TimeSpan.FromMilliseconds(10),
                () =>
                $"PlayCount={player.PlayCount}, IsPlaying={player.IsPlaying}, SampledTime={player.SampledTimeMilliseconds}, Wraps={player.ObservedWraps}");

            Assert.Equal(1, player.PlayCount);
            Assert.True(player.IsPlaying);
        }
        finally
        {
            await renderer.DisposeAsync();
            await DestroyHostAsync(dispatcher, host);
        }
    }

    [Fact]
    [Trait("Category", "LibVLCIntegration")]
    public async Task CreatePlayDispose_TwoEqualBatches_UsesDistinctPlayersAndHandleGrowthPlateaus()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        await using var runtime = CreateRuntimeOrSkip();
        var capturing = new CapturingRuntime(runtime, retainPlayers: false);
        var source = TinyMp4TestAsset.Create(_directory, "cycles.mp4");
        var host = await CreateHostAsync(dispatcher);
        var process = Process.GetCurrentProcess();
        try
        {
            await RunCycleAsync(dispatcher, capturing, host, source);
            var baseline = await MeasureMinimumHandleCountAsync(process, TimeSpan.FromSeconds(2));

            for (var cycle = 0; cycle < 25; cycle++)
            {
                await RunCycleAsync(dispatcher, capturing, host, source);
            }

            var firstBatch = await MeasureMinimumHandleCountAsync(process, TimeSpan.FromSeconds(2));
            Assert.InRange(firstBatch, 0, baseline + HandleTolerance);

            for (var cycle = 0; cycle < 25; cycle++)
            {
                await RunCycleAsync(dispatcher, capturing, host, source);
            }

            var secondBatch = await MeasureMinimumHandleCountAsync(process, TimeSpan.FromSeconds(2));
            Assert.InRange(secondBatch, 0, firstBatch + PlateauTolerance);
            Assert.Equal(51, capturing.CreatedPlayerCount);
        }
        finally
        {
            await DestroyHostAsync(dispatcher, host);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static async Task RunCycleAsync(
        EngineStaDispatcher dispatcher,
        CapturingRuntime runtime,
        nint host,
        string source)
    {
        var renderer = new VideoRenderer(
            dispatcher, runtime, new FakeVideoProbeService(), VideoSurfaceWindowFactory.Instance, VideoRendererOptions.Default);
        await ActivateAsync(dispatcher, renderer, host, source);
        var player = runtime.LatestPlayer;
        Assert.Equal(1, player.PlayCount);
        await renderer.DisposeAsync();
        Assert.False(NativeMethods.IsWindow(player.AssignedHwnd));
        Assert.Equal(1, player.DisposeCount);
        runtime.ReleaseLatestPlayer();
    }

    private static Task ActivateAsync(
        EngineStaDispatcher dispatcher,
        VideoRenderer renderer,
        nint host,
        string source) =>
        dispatcher.InvokeAsync(async token =>
        {
            await renderer.InitializeAsync(Context(host), token);
            await renderer.LoadAsync(VideoSource.Create(source), token);
            await renderer.ActivateAsync(token);
        }, TestContext.Current.CancellationToken);

    private static RendererContext Context(nint host)
    {
        var bounds = new DisplayViewport(0, 0, 320, 180);
        var identity = new MonitorIdentity("LIBVLC-HIDDEN");
        var evidence = new MonitorIdentityEvidence(0, 0, 0, null, null, null, null, null, null, identity.Key, bounds);
        var monitor = new MonitorDescriptor(identity, evidence, identity.Key, bounds, bounds, 96, 1, DisplayOrientation.Landscape, true);
        return new RendererContext(host, monitor, bounds, bounds, new OutputWallpaperSettings(FitMode.Cover, 30, 42));
    }

    private static LibVlcRuntime CreateRuntimeOrSkip()
    {
        try
        {
            return new LibVlcRuntime();
        }
        catch (Exception exception) when (exception is
            PlatformNotSupportedException or FileNotFoundException or DllNotFoundException or
            BadImageFormatException or VLCException)
        {
            Assert.Skip($"Pinned LibVLC x64 runtime unavailable: {exception.Message}");
            throw;
        }
    }

    private static async Task<nint> CreateHostAsync(EngineStaDispatcher dispatcher)
    {
        var hwnd = await dispatcher.InvokeAsync(_ =>
        {
            var created = NativeMethods.CreateWindowEx(
                0x08000080u, "STATIC", "VibeWallpaper.LibVLC.Integration", 0x80000000u,
                0, 0, 320, 180, 0, 0, 0, 0);
            return ValueTask.FromResult(created);
        }, TestContext.Current.CancellationToken);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"Could not create hidden test HWND (error {Marshal.GetLastPInvokeError()}).");
        }

        return hwnd;
    }

    private static Task DestroyHostAsync(EngineStaDispatcher dispatcher, nint host) =>
        dispatcher.InvokeAsync(_ =>
        {
            if (NativeMethods.IsWindow(host) && !NativeMethods.DestroyWindow(host))
            {
                throw new InvalidOperationException($"Could not destroy hidden test HWND (error {Marshal.GetLastPInvokeError()}).");
            }

            return ValueTask.CompletedTask;
        });

    private static Task<T> OnEngineAsync<T>(EngineStaDispatcher dispatcher, Func<T> read) =>
        dispatcher.InvokeAsync(_ => ValueTask.FromResult(read()), TestContext.Current.CancellationToken);

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        TimeSpan? sampleInterval = null,
        Func<string>? timeoutDetails = null)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        using var timer = new PeriodicTimer(sampleInterval ?? TimeSpan.FromMilliseconds(20));
        while (Stopwatch.GetTimestamp() < deadline &&
               await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken))
        {
            if (await predicate())
            {
                return;
            }
        }

        Assert.Fail($"Condition did not become true within {timeout}. {timeoutDetails?.Invoke()}");
    }

    private static async Task WaitForDurationAsync(TimeSpan duration)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(duration.TotalSeconds * Stopwatch.Frequency);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        while (Stopwatch.GetTimestamp() < deadline)
        {
            _ = await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken);
        }
    }

    private static void CollectReleasedResources()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    private static async Task<int> MeasureMinimumHandleCountAsync(Process process, TimeSpan observationWindow)
    {
        var minimum = int.MaxValue;
        var deadline = Stopwatch.GetTimestamp() +
            (long)(observationWindow.TotalSeconds * Stopwatch.Frequency);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(50));
        while (Stopwatch.GetTimestamp() < deadline)
        {
            CollectReleasedResources();
            process.Refresh();
            minimum = Math.Min(minimum, process.HandleCount);
            _ = await timer.WaitForNextTickAsync(TestContext.Current.CancellationToken);
        }

        return minimum;
    }

    private sealed class CapturingRuntime : ILibVlcRuntime
    {
        private readonly ILibVlcRuntime _inner;
        private readonly bool _retainPlayers;
        private CapturingPlayer? _latestPlayer;

        public CapturingRuntime(ILibVlcRuntime inner, bool retainPlayers = true)
        {
            _inner = inner;
            _retainPlayers = retainPlayers;
        }

        public List<CapturingPlayer> Players { get; } = [];
        public int CreatedPlayerCount { get; private set; }
        public CapturingPlayer LatestPlayer => _latestPlayer ?? throw new InvalidOperationException("No player has been created.");
        public bool HardwareDecodingRequested => _inner.HardwareDecodingRequested;
        public string Version => _inner.Version;
        public ILibVlcPlayer CreatePlayer()
        {
            var player = new CapturingPlayer(_inner.CreatePlayer());
            CreatedPlayerCount++;
            _latestPlayer = player;
            if (_retainPlayers)
            {
                Players.Add(player);
            }

            return player;
        }

        public void ReleaseLatestPlayer() => _latestPlayer = null;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingPlayer : ILibVlcPlayer
    {
        private readonly ILibVlcPlayer _inner;
        private long _lastObservedTimeMilliseconds = -1;
        public CapturingPlayer(ILibVlcPlayer inner)
        {
            _inner = inner;
            _inner.PlaybackProgressed += OnPlaybackProgressed;
        }
        public nint AssignedHwnd { get; private set; }
        public int DisposeCount { get; private set; }
        public int PlayCount { get; private set; }
        public int ObservedWraps { get; private set; }
        public long SampledTimeMilliseconds { get; private set; }
        public nint Hwnd { set { AssignedHwnd = value; _inner.Hwnd = value; } }
        public long TimeMilliseconds { get => _inner.TimeMilliseconds; set => _inner.TimeMilliseconds = value; }
        public bool IsPlaying => _inner.IsPlaying;
        public bool IsMuted { get => _inner.IsMuted; set => _inner.IsMuted = value; }
        public int VolumePercent { get => _inner.VolumePercent; set => _inner.VolumePercent = value; }
        public event EventHandler? EndReached { add => _inner.EndReached += value; remove => _inner.EndReached -= value; }
        public event EventHandler<VideoFaultEventArgs>? EncounteredError { add => _inner.EncounteredError += value; remove => _inner.EncounteredError -= value; }
        public event EventHandler<VideoPlaybackProgressEventArgs>? PlaybackProgressed { add => _inner.PlaybackProgressed += value; remove => _inner.PlaybackProgressed -= value; }
        public void ApplySourceCrop(NormalizedSourceRect crop, int videoWidth, int videoHeight) =>
            _inner.ApplySourceCrop(crop, videoWidth, videoHeight);
        public void Open(string absolutePath, VideoMediaOpenOptions options) => _inner.Open(absolutePath, options);
        public void Play() { PlayCount++; _inner.Play(); }
        public void Pause() => _inner.Pause();
        public void Stop() => _inner.Stop();
        public void Dispose()
        {
            DisposeCount++;
            _inner.PlaybackProgressed -= OnPlaybackProgressed;
            _inner.Dispose();
        }

        private void OnPlaybackProgressed(object? sender, VideoPlaybackProgressEventArgs args)
        {
            if (_lastObservedTimeMilliseconds > 0 && args.TimeMilliseconds > 0 && args.TimeMilliseconds < _lastObservedTimeMilliseconds)
            {
                ObservedWraps++;
            }

            _lastObservedTimeMilliseconds = args.TimeMilliseconds;
            SampledTimeMilliseconds = args.TimeMilliseconds;
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, CharSet = CharSet.Unicode)]
        internal static extern nint CreateWindowEx(
            uint extendedStyle, string className, string windowName, uint style,
            int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint hwnd);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint hwnd);
    }
}
