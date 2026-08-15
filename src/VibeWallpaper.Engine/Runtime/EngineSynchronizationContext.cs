namespace VibeWallpaper.Engine.Runtime;

internal sealed class EngineSynchronizationContext : SynchronizationContext
{
    private readonly EngineStaDispatcher? _dispatcher;
    private readonly EngineContinuationOwner? _owner;

    internal EngineSynchronizationContext(EngineStaDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    internal EngineSynchronizationContext(EngineContinuationOwner owner)
    {
        _owner = owner;
    }

    public override SynchronizationContext CreateCopy() => this;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var continuation = () => callback(state);
        if (_owner is not null)
        {
            _owner.Post(continuation);
        }
        else
        {
            _dispatcher!.PostContinuation(owner: null, continuation);
        }
    }

    public override void Send(SendOrPostCallback callback, object? state)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var hasThreadAccess = _owner?.HasThreadAccess ?? _dispatcher!.HasThreadAccess;
        if (!hasThreadAccess)
        {
            throw new NotSupportedException("Cross-thread Send is not supported by the engine dispatcher.");
        }

        callback(state);
    }
}

internal sealed class EngineContinuationOwner
{
    private const int Active = 0;
    private const int CancellationCallbacksPending = 1;
    private const int CancellationCallbacksComplete = 2;

    private readonly WeakReference<EngineStaDispatcher> _weakDispatcher;
    private EngineStaDispatcher? _dispatcher;
    private int _cancellationState;
    private int _isAttached = 1;

    internal EngineContinuationOwner(EngineStaDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _weakDispatcher = new WeakReference<EngineStaDispatcher>(dispatcher);
        SynchronizationContext = new EngineSynchronizationContext(this);
    }

    internal EngineSynchronizationContext SynchronizationContext { get; }
    internal bool HasThreadAccess => ResolveDispatcher()?.HasThreadAccess == true;
    internal bool IsAttached => Volatile.Read(ref _isAttached) != 0;
    internal bool CanRunNormally =>
        Volatile.Read(ref _cancellationState) != CancellationCallbacksPending;
    internal bool CanRunCooperatively =>
        IsAttached && Volatile.Read(ref _cancellationState) == CancellationCallbacksComplete;

    internal bool BeginCancellation() =>
        Interlocked.CompareExchange(
            ref _cancellationState,
            CancellationCallbacksPending,
            Active) == Active;

    internal void TrackCancellation(Task cancellationCompletion)
    {
        _ = CompleteCancellationAsync(cancellationCompletion);
    }

    internal void Post(Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        var dispatcher = ResolveDispatcher();
        dispatcher?.PostContinuation(this, continuation);
    }

    internal void Detach()
    {
        Interlocked.Exchange(ref _isAttached, 0);
        Interlocked.Exchange(ref _dispatcher, null);
    }

    private async Task CompleteCancellationAsync(Task cancellationCompletion)
    {
        try
        {
            await cancellationCompletion.ConfigureAwait(false);
        }
        catch
        {
            // Callback failure still means the callback sequence is terminal and safe to unwind.
        }

        if (Interlocked.CompareExchange(
                ref _cancellationState,
                CancellationCallbacksComplete,
                CancellationCallbacksPending) != CancellationCallbacksPending)
        {
            return;
        }

        var dispatcher = ResolveDispatcher();
        dispatcher?.SignalContinuationOwnerReady();
    }

    private EngineStaDispatcher? ResolveDispatcher()
    {
        var dispatcher = Volatile.Read(ref _dispatcher);
        if (dispatcher is not null)
        {
            return dispatcher;
        }

        return _weakDispatcher.TryGetTarget(out dispatcher) ? dispatcher : null;
    }
}
