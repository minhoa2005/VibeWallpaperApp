using System.Collections.Concurrent;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Rendering.Video;

namespace VibeWallpaper.Tests.Runtime.Fakes;

internal sealed class RendererBarrier(bool observeCancellation = true)
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource ReleaseSource { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool ObserveCancellation { get; } = observeCancellation;
    public bool CancellationWasRequested { get; private set; }
    public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => ReleaseSource.TrySetResult();
    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        CancellationWasRequested = cancellationToken.IsCancellationRequested;
        using var registration = cancellationToken.Register(
            () => CancellationObserved.TrySetResult(),
            useSynchronizationContext: false);
        if (ObserveCancellation)
        {
            await ReleaseSource.Task.WaitAsync(cancellationToken);
        }
        else
        {
            await ReleaseSource.Task;
        }
    }
}

internal sealed class FakeWallpaperRendererFactory : IRendererFactory
{
    private readonly ConcurrentDictionary<string, FakeRendererPlan> _plans = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<FakeWallpaperRenderer>> _renderers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<nint, FakeWallpaperRenderer> _activeByHost = new();

    public IWallpaperRenderer Create(WallpaperDefinition definition)
    {
        var renderer = new FakeWallpaperRenderer(definition, Plan(definition.Name), this);
        lock (_renderers)
        {
            if (!_renderers.TryGetValue(definition.Name, out var instances))
            {
                instances = [];
                _renderers[definition.Name] = instances;
            }

            instances.Add(renderer);
        }

        return renderer;
    }

    public FakeRendererPlan Plan(string name) => _plans.GetOrAdd(name, static _ => new FakeRendererPlan());
    public FakeWallpaperRenderer Renderer(string name) => _renderers[name].Last();
    public IReadOnlyList<FakeWallpaperRenderer> Renderers(string name)
    {
        lock (_renderers) return _renderers[name].ToArray();
    }
    public FakeWallpaperRenderer? Active(nint host) => _activeByHost.GetValueOrDefault(host);

    internal void Activate(nint host, FakeWallpaperRenderer renderer) => _activeByHost[host] = renderer;
    internal void Stop(nint host, FakeWallpaperRenderer renderer) =>
        ((ICollection<KeyValuePair<nint, FakeWallpaperRenderer>>)_activeByHost)
            .Remove(new KeyValuePair<nint, FakeWallpaperRenderer>(host, renderer));
}

internal sealed class FakeRendererPlan
{
    public RendererBarrier? LoadBarrier { get; set; }
    public nint? LoadBarrierHost { get; set; }
    public RendererBarrier? ActivationBarrier { get; set; }
    public nint? ActivationBarrierHost { get; set; }
    public RendererBarrier? PerformanceBarrier { get; set; }
    public int PerformanceBarrierOnCall { get; set; } = 1;
    public nint? PerformanceBarrierHost { get; set; }
    public Exception? InitializeFailure { get; set; }
    public Exception? LoadFailure { get; set; }
    public nint? LoadFailureHost { get; set; }
    public Exception? ActivationFailure { get; set; }
    public nint? ActivationFailureHost { get; set; }
    public Exception? StopFailure { get; set; }
    public nint? StopFailureHost { get; set; }
    public Exception? DisposeFailure { get; set; }
    public nint? DisposeFailureHost { get; set; }
    public Exception? PerformanceFailure { get; set; }
    public TimeSpan? ReportedDuration { get; set; }
    public Func<nint, TimeSpan>? ReportedDurationResolver { get; set; }
}

internal sealed class FakeWallpaperRenderer(
    WallpaperDefinition definition,
    FakeRendererPlan plan,
    FakeWallpaperRendererFactory owner) : IWallpaperRenderer, IVideoAudioEndpoint, IVideoSynchronizationEndpoint
{
    private readonly RendererStateMachine _state = new();
    private int _disposeCount;
    private int _activateCount;
    private int _performanceCallCount;
    private nint _host;

    public string Name { get; } = definition.Name;
    public RendererLifecycle Lifecycle => _state.Lifecycle;
    public PerformanceState PerformanceState => _state.PerformanceState;
    public RendererCapabilities Capabilities { get; } = RendererCapabilities.Web;
    public int DisposeCount => Volatile.Read(ref _disposeCount);
    public int ActivateCount => Volatile.Read(ref _activateCount);
    public int PerformanceCallCount => Volatile.Read(ref _performanceCallCount);
    public bool RenderedRunningFrame { get; private set; }
    public RendererContext? Context { get; private set; }
    public RendererPerformanceRequest LastPerformanceRequest { get; private set; } = new(PerformanceState.Running);
    public MonitorIdentity Output => Context?.Monitor.Identity ?? throw new InvalidOperationException("Not initialized.");
    public bool IsConnected => Lifecycle is not RendererLifecycle.Faulted and not RendererLifecycle.Disposed;
    public bool IsActiveVideo => definition.Source is VideoSource && Lifecycle == RendererLifecycle.Active;
    public bool IsSuspended => PerformanceState == PerformanceState.Suspended;
    public int PersistedVolumePercent => Context?.Settings.VolumePercent ?? definition.VolumePercent;
    public bool IsMuted { get; private set; } = true;
    public int VolumePercent { get; private set; }
    public string Id => $"{Name}:{Output.Key}";
    public TimeSpan Duration =>
        plan.ReportedDurationResolver?.Invoke(_host) ??
        plan.ReportedDuration ??
        (definition.Source is VideoSource ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
    public TimeSpan Position { get; set; }
    public List<TimeSpan> Seeks { get; } = [];
    public TaskCompletionSource SeekObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public IVideoResumeObserver? ResumeObserver { get; private set; }
    public List<string> AudioEvents { get; } = [];
    public TaskCompletionSource SuspendedSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ThrottledSet { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task InitializeAsync(RendererContext context, CancellationToken cancellationToken)
    {
        if (plan.InitializeFailure is { } failure) throw failure;
        Context = context;
        _host = context.HostHwnd;
        _state.TransitionTo(RendererLifecycle.Initializing);
        return Task.CompletedTask;
    }

    public async Task LoadAsync(WallpaperSource source, CancellationToken cancellationToken)
    {
        _state.TransitionTo(RendererLifecycle.Loading);
        if (plan.LoadBarrier is { } barrier &&
            (plan.LoadBarrierHost is null || plan.LoadBarrierHost == _host))
        {
            barrier.Started.TrySetResult();
            await barrier.WaitAsync(cancellationToken);
        }

        if (plan.LoadFailure is { } failure &&
            (plan.LoadFailureHost is null || plan.LoadFailureHost == _host)) throw failure;
        _state.TransitionTo(RendererLifecycle.Ready);
    }

    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _activateCount);
        if (plan.ActivationBarrier is { } barrier &&
            (plan.ActivationBarrierHost is null || plan.ActivationBarrierHost == _host))
        {
            barrier.Started.TrySetResult();
            await barrier.WaitAsync(cancellationToken);
        }

        if (plan.ActivationFailure is { } failure &&
            (plan.ActivationFailureHost is null || plan.ActivationFailureHost == _host)) throw failure;
        if (_state.Lifecycle == RendererLifecycle.Ready)
        {
            _state.TransitionTo(RendererLifecycle.Active);
        }
        RenderedRunningFrame |= PerformanceState == PerformanceState.Running;
        owner.Activate(_host, this);
    }

    public async Task ApplyPerformanceAsync(RendererPerformanceRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var previous = PerformanceState;
        var call = Interlocked.Increment(ref _performanceCallCount);
        if (plan.PerformanceBarrier is { } barrier &&
            call == plan.PerformanceBarrierOnCall &&
            (plan.PerformanceBarrierHost is null || plan.PerformanceBarrierHost == _host))
        {
            barrier.Started.TrySetResult();
            await barrier.WaitAsync(cancellationToken);
        }

        LastPerformanceRequest = request;
        _state.SetPerformanceState(request.State);
        if (previous == PerformanceState.Suspended && request.State == PerformanceState.Running)
        {
            ResumeObserver?.NotifyResumed(Id);
        }
        if (plan.PerformanceFailure is { } failure) throw failure;
        if (request.State == PerformanceState.Suspended) SuspendedSet.TrySetResult();
        if (request.State == PerformanceState.Throttled) ThrottledSet.TrySetResult();
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (plan.StopFailure is { } failure &&
            (plan.StopFailureHost is null || plan.StopFailureHost == _host)) throw failure;
        owner.Stop(_host, this);
        _state.Stop();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Increment(ref _disposeCount) == 1)
        {
            owner.Stop(_host, this);
            _state.Dispose();
            if (plan.DisposeFailure is { } failure &&
                (plan.DisposeFailureHost is null || plan.DisposeFailureHost == _host))
            {
                throw failure;
            }
        }

        return ValueTask.CompletedTask;
    }

    public void SetMuted(bool muted)
    {
        IsMuted = muted;
        AudioEvents.Add(muted ? "mute" : "unmute");
    }

    public void SetVolume(int volumePercent)
    {
        VolumePercent = volumePercent;
        AudioEvents.Add($"volume:{volumePercent}");
    }

    public void Seek(TimeSpan position)
    {
        Position = position;
        Seeks.Add(position);
        SeekObserved.TrySetResult();
    }

    public void AttachResumeObserver(IVideoResumeObserver observer) => ResumeObserver = observer;
}
