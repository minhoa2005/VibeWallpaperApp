using System.Runtime.InteropServices;
using System.Runtime.ExceptionServices;
using LibVLCSharp.Shared;
using VibeWallpaper.Engine.Core.Rendering;
using VlcMedia = LibVLCSharp.Shared.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace VibeWallpaper.Engine.Rendering.Video;

public sealed class LibVlcRuntime : ILibVlcRuntime
{
    internal const string BackendName = "libvlc";

    private static readonly string[] RuntimeOptions =
        ["--no-video-title-show", "--avcodec-hw=any"];
    private readonly LibVLC _libVlc;
    private bool _disposed;

    public LibVlcRuntime(string? runtimePath = null)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("The pinned video runtime requires a 64-bit Windows process.");
        }

        var path = Path.GetFullPath(runtimePath ?? Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64"));
        ValidateRuntime(path);
        LibVLCSharp.Shared.Core.Initialize(path);
        _libVlc = CreateNativeRuntime();
        Version = _libVlc.Version;
    }

    public bool HardwareDecodingRequested => true;
    public string Version { get; }

    public ILibVlcPlayer CreatePlayer()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return new LibVlcPlayer(_libVlc);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _libVlc.Dispose();
        return ValueTask.CompletedTask;
    }

    private static LibVLC CreateNativeRuntime() => new(RuntimeOptions);

    private static void ValidateRuntime(string path)
    {
        if (!File.Exists(Path.Combine(path, "libvlc.dll")) ||
            !File.Exists(Path.Combine(path, "libvlccore.dll")) ||
            !Directory.Exists(Path.Combine(path, "plugins")))
        {
            throw new FileNotFoundException(
                $"Pinned LibVLC x64 runtime is incomplete at '{path}'. Expected libvlc.dll, libvlccore.dll, and plugins.");
        }
    }

    private sealed class LibVlcPlayer : ILibVlcPlayer
    {
        private readonly LibVLC _libVlc;
        private readonly VlcMediaPlayer _player;
        private VlcMedia? _media;
        private bool _stopped;
        private bool _disposed;

        internal LibVlcPlayer(LibVLC libVlc)
        {
            _libVlc = libVlc;
            _player = new VlcMediaPlayer(libVlc)
            {
                EnableHardwareDecoding = true,
            };
            _player.EndReached += OnEndReached;
            _player.EncounteredError += OnEncounteredError;
            _player.TimeChanged += OnTimeChanged;
        }

        public nint Hwnd { set => _player.Hwnd = value; }
        public long TimeMilliseconds { get => _player.Time; set => _player.Time = value; }
        public bool IsPlaying => _player.IsPlaying;
        public bool IsMuted { get => _player.Mute; set => _player.Mute = value; }
        public int VolumePercent { get => _player.Volume; set => _player.Volume = value; }

        public event EventHandler? EndReached;
        public event EventHandler<VideoFaultEventArgs>? EncounteredError;
        public event EventHandler<VideoPlaybackProgressEventArgs>? PlaybackProgressed;

        public void ApplySourceCrop(NormalizedSourceRect crop, int videoWidth, int videoHeight)
        {
            ArgumentNullException.ThrowIfNull(crop);
            if (videoWidth <= 0) throw new ArgumentOutOfRangeException(nameof(videoWidth));
            if (videoHeight <= 0) throw new ArgumentOutOfRangeException(nameof(videoHeight));
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (crop == new NormalizedSourceRect(0, 0, 1, 1))
            {
                _player.CropGeometry = null;
                return;
            }

            var x = Math.Clamp((int)Math.Round(crop.X * videoWidth), 0, videoWidth - 1);
            var y = Math.Clamp((int)Math.Round(crop.Y * videoHeight), 0, videoHeight - 1);
            var width = Math.Clamp((int)Math.Round(crop.Width * videoWidth), 1, videoWidth - x);
            var height = Math.Clamp((int)Math.Round(crop.Height * videoHeight), 1, videoHeight - y);
            _player.CropGeometry = FormattableString.Invariant($"{width}x{height}+{x}+{y}");
        }

        public void Open(string absolutePath, VideoMediaOpenOptions options)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (string.IsNullOrWhiteSpace(absolutePath) || !Path.IsPathFullyQualified(absolutePath))
            {
                throw new ArgumentException("An absolute media path is required.", nameof(absolutePath));
            }

            ArgumentNullException.ThrowIfNull(options);

            if (_media is not null)
            {
                throw new InvalidOperationException("This player already owns media.");
            }

            _media = new VlcMedia(_libVlc, Path.GetFullPath(absolutePath), FromType.FromPath);
            if (options.Loop)
            {
                _media.AddOption(":input-repeat=65535");
            }

            _player.Media = _media;
            _stopped = false;
        }

        public void Play()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_player.Play())
            {
                throw new InvalidOperationException("LibVLC rejected the playback request.");
            }
        }

        public void Pause()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _player.SetPause(true);
        }

        public void Stop()
        {
            if (_disposed || _stopped)
            {
                return;
            }

            _stopped = true;
            Exception? failure = null;
            CaptureFailure(ref failure, _player.Stop);
            CaptureFailure(ref failure, () => _player.Hwnd = 0);
            CaptureFailure(ref failure, () => _player.Media = null);
            var media = _media;
            _media = null;
            if (media is not null)
            {
                CaptureFailure(ref failure, media.Dispose);
            }

            ThrowIfFailed(failure);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Exception? failure = null;
            CaptureFailure(ref failure, () => _player.EndReached -= OnEndReached);
            CaptureFailure(ref failure, () => _player.EncounteredError -= OnEncounteredError);
            CaptureFailure(ref failure, () => _player.TimeChanged -= OnTimeChanged);
            CaptureFailure(ref failure, Stop);
            _disposed = true;
            CaptureFailure(ref failure, _player.Dispose);
            ThrowIfFailed(failure);
        }

        private void OnEndReached(object? sender, EventArgs args) => EndReached?.Invoke(this, EventArgs.Empty);

        private void OnEncounteredError(object? sender, EventArgs args) =>
            EncounteredError?.Invoke(this, new VideoFaultEventArgs("video.libvlc.encountered_error", "LibVLC reported a playback error."));

        private void OnTimeChanged(object? sender, MediaPlayerTimeChangedEventArgs args)
        {
            if (args.Time < 0)
            {
                return;
            }

            PlaybackProgressed?.Invoke(this, new VideoPlaybackProgressEventArgs(args.Time));
        }

        private static void CaptureFailure(ref Exception? failure, Action action)
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure ??= exception;
            }
        }

        private static void ThrowIfFailed(Exception? failure)
        {
            if (failure is not null)
            {
                ExceptionDispatchInfo.Capture(failure).Throw();
            }
        }
    }
}
