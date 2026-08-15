namespace VibeWallpaper.Engine.Runtime;

internal abstract class EngineWorkItem
{
    private const int Queued = 0;
    private const int Started = 1;
    private const int Terminal = 2;

    private readonly CancellationToken _callerCancellationToken;
    private readonly object _cancellationGate = new();
    private CancellationTokenRegistration _queuedCancellationRegistration;
    private CancellationTokenRegistration _startedCallerCancellationRegistration;
    private CancellationTokenSource? _linkedCancellation;
    private Action<EngineWorkItem>? _terminalCallback;
    private Action<EngineWorkItem>? _cancellationRequestCallback;
    private EngineContinuationOwner? _continuationOwner;
    private int _registrationInitialized;
    private int _startedRegistrationInitialized;
    private int _cooperativeCancellationRequested;
    private int _state;
    private bool _cooperativeCancellationStarted;
    private Task _cooperativeCancellationCompletion = Task.CompletedTask;

    protected EngineWorkItem(CancellationToken callerCancellationToken)
    {
        _callerCancellationToken = callerCancellationToken;
        if (callerCancellationToken.CanBeCanceled)
        {
            _queuedCancellationRegistration = callerCancellationToken.Register(
                static state => ((EngineWorkItem)state!).CancelBeforeStart(),
                this);
            Volatile.Write(ref _registrationInitialized, 1);
            if (Volatile.Read(ref _state) == Terminal)
            {
                DisposeQueuedCancellationRegistration();
            }
        }
    }

    internal abstract Task Completion { get; }
    internal abstract Task Observation { get; }

    internal bool TryBeginStart(CancellationToken shutdownCancellationToken)
    {
        if (_callerCancellationToken.IsCancellationRequested)
        {
            CancelBeforeStart();
            return false;
        }

        if (shutdownCancellationToken.IsCancellationRequested)
        {
            CancelBeforeStart(shutdownCancellationToken);
            return false;
        }

        if (Interlocked.CompareExchange(ref _state, Started, Queued) != Queued)
        {
            return false;
        }

        DisposeQueuedCancellationRegistration();
        _linkedCancellation = new CancellationTokenSource();
        return true;
    }

    internal void Prepare(
        EngineContinuationOwner continuationOwner,
        Action<EngineWorkItem> terminalCallback,
        Action<EngineWorkItem> cancellationRequestCallback)
    {
        ArgumentNullException.ThrowIfNull(continuationOwner);
        ArgumentNullException.ThrowIfNull(terminalCallback);
        ArgumentNullException.ThrowIfNull(cancellationRequestCallback);
        if (Interlocked.CompareExchange(ref _continuationOwner, continuationOwner, null) is not null)
        {
            throw new InvalidOperationException("A work item continuation owner can only be registered once.");
        }

        if (Interlocked.CompareExchange(ref _terminalCallback, terminalCallback, null) is not null)
        {
            throw new InvalidOperationException("A work item terminal callback can only be registered once.");
        }

        if (Interlocked.CompareExchange(
                ref _cancellationRequestCallback,
                cancellationRequestCallback,
                null) is not null)
        {
            throw new InvalidOperationException("A work item cancellation callback can only be registered once.");
        }

        if (_callerCancellationToken.CanBeCanceled)
        {
            _startedCallerCancellationRegistration = _callerCancellationToken.UnsafeRegister(
                static state => ((EngineWorkItem)state!).RequestCooperativeCancellation(),
                this);
            Volatile.Write(ref _startedRegistrationInitialized, 1);
            if (Volatile.Read(ref _state) == Terminal)
            {
                DisposeStartedCallerCancellationRegistration();
            }
        }
    }

    internal void RequestCooperativeCancellation()
    {
        if (Volatile.Read(ref _state) != Started ||
            Interlocked.Exchange(ref _cooperativeCancellationRequested, 1) != 0)
        {
            return;
        }

        Volatile.Read(ref _cancellationRequestCallback)?.Invoke(this);
    }

    internal Task Run()
    {
        if (Volatile.Read(ref _state) != Started)
        {
            return Observation;
        }

        var cancellationToken = _linkedCancellation!.Token;
        if (cancellationToken.IsCancellationRequested)
        {
            CompleteCanceledBeforeRun(cancellationToken);
            return Observation;
        }

        RunCore(cancellationToken);
        return Observation;
    }

    internal Task BeginCooperativeCancellation()
    {
        lock (_cancellationGate)
        {
            if (_cooperativeCancellationStarted)
            {
                return _cooperativeCancellationCompletion;
            }

            _cooperativeCancellationStarted = true;
            var continuationOwner = Volatile.Read(ref _continuationOwner);
            if (continuationOwner is null || !continuationOwner.BeginCancellation())
            {
                return _cooperativeCancellationCompletion;
            }

            try
            {
                _cooperativeCancellationCompletion =
                    Volatile.Read(ref _linkedCancellation)?.CancelAsync() ?? Task.CompletedTask;
            }
            catch (Exception exception)
            {
                _cooperativeCancellationCompletion = Task.FromException(exception);
            }

            continuationOwner.TrackCancellation(_cooperativeCancellationCompletion);
            return _cooperativeCancellationCompletion;
        }
    }

    internal void CancelBeforeStart(CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _state, Terminal, Queued) != Queued)
        {
            return;
        }

        DisposeQueuedCancellationRegistration();
        SetCanceled(cancellationToken.CanBeCanceled ? cancellationToken : _callerCancellationToken);
    }

    internal void ForceCancel(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _state, Terminal, Started) != Started)
        {
            CancelBeforeStart(cancellationToken);
            return;
        }

        FinishStarted();
        SetCanceled(cancellationToken);
    }

    internal void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var previous = Interlocked.Exchange(ref _state, Terminal);
        if (previous == Terminal)
        {
            return;
        }

        if (previous == Queued)
        {
            DisposeQueuedCancellationRegistration();
        }
        else
        {
            FinishStarted();
        }

        SetException(exception);
    }

    protected abstract void RunCore(CancellationToken cancellationToken);
    protected abstract void SetCanceled(CancellationToken cancellationToken);
    protected abstract void SetException(Exception exception);

    protected bool TryCompleteStarted()
    {
        if (Interlocked.CompareExchange(ref _state, Terminal, Started) != Started)
        {
            return false;
        }

        FinishStarted();
        return true;
    }

    private void FinishStarted()
    {
        DisposeStartedCallerCancellationRegistration();
        Interlocked.Exchange(ref _cancellationRequestCallback, null);
        Interlocked.Exchange(ref _continuationOwner, null)?.Detach();
        var linkedCancellation = Interlocked.Exchange(ref _linkedCancellation, null);
        Interlocked.Exchange(ref _terminalCallback, null)?.Invoke(this);
        if (linkedCancellation is not null)
        {
            QueueLinkedCancellationDisposal(linkedCancellation);
        }
    }

    private void CompleteCanceledBeforeRun(CancellationToken cancellationToken)
    {
        if (TryCompleteStarted())
        {
            SetCanceled(cancellationToken);
        }
    }

    private void DisposeQueuedCancellationRegistration()
    {
        if (Interlocked.CompareExchange(ref _registrationInitialized, 2, 1) == 1)
        {
            DisposeRegistrationWithoutBlocking(_queuedCancellationRegistration);
        }
    }

    private void DisposeStartedCallerCancellationRegistration()
    {
        if (Interlocked.CompareExchange(ref _startedRegistrationInitialized, 2, 1) == 1)
        {
            DisposeRegistrationWithoutBlocking(_startedCallerCancellationRegistration);
        }
    }

    private void QueueLinkedCancellationDisposal(CancellationTokenSource linkedCancellation)
    {
        if (Monitor.TryEnter(_cancellationGate))
        {
            Task cancellationCompletion;
            try
            {
                cancellationCompletion = _cooperativeCancellationCompletion;
            }
            finally
            {
                Monitor.Exit(_cancellationGate);
            }

            StartLinkedCancellationDisposal(cancellationCompletion, linkedCancellation);
            return;
        }

        _ = ThreadPool.UnsafeQueueUserWorkItem(
            static state => state.WorkItem.BeginLinkedCancellationDisposal(state.LinkedCancellation),
            new LinkedCancellationCleanup(this, linkedCancellation),
            preferLocal: false);
    }

    private void BeginLinkedCancellationDisposal(CancellationTokenSource linkedCancellation)
    {
        Task cancellationCompletion;
        lock (_cancellationGate)
        {
            cancellationCompletion = _cooperativeCancellationCompletion;
        }

        StartLinkedCancellationDisposal(cancellationCompletion, linkedCancellation);
    }

    private static void StartLinkedCancellationDisposal(
        Task cancellationCompletion,
        CancellationTokenSource linkedCancellation)
    {
        if (cancellationCompletion.IsCompletedSuccessfully)
        {
            linkedCancellation.Dispose();
            return;
        }

        _ = DisposeLinkedCancellationAsync(cancellationCompletion, linkedCancellation);
    }

    private static void DisposeRegistrationWithoutBlocking(CancellationTokenRegistration registration)
    {
        var disposal = registration.DisposeAsync();
        if (!disposal.IsCompletedSuccessfully)
        {
            _ = ObserveRegistrationDisposalAsync(disposal);
        }
    }

    private static async Task ObserveRegistrationDisposalAsync(ValueTask disposal)
    {
        try
        {
            await disposal.ConfigureAwait(false);
        }
        catch
        {
            // Registration cleanup cannot change an already-terminal caller result.
        }
    }

    private static async Task DisposeLinkedCancellationAsync(
        Task cancellationCompletion,
        CancellationTokenSource linkedCancellation)
    {
        try
        {
            await cancellationCompletion.ConfigureAwait(false);
        }
        catch
        {
            // Callback failure is already observed by dispatcher cancellation tracking.
        }
        finally
        {
            linkedCancellation.Dispose();
        }
    }

    private readonly record struct LinkedCancellationCleanup(
        EngineWorkItem WorkItem,
        CancellationTokenSource LinkedCancellation);
}

internal sealed class EngineWorkItem<T>(
    Func<CancellationToken, ValueTask<T>> action,
    CancellationToken callerCancellationToken) : EngineWorkItem(callerCancellationToken)
{
    private readonly TaskCompletionSource<T> _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private Task _observation = Task.CompletedTask;

    internal override Task Completion => _completion.Task;
    internal override Task Observation => _observation;
    internal Task<T> TypedCompletion => _completion.Task;

    protected override void RunCore(CancellationToken cancellationToken)
    {
        try
        {
            _observation = ObserveAsync(action(cancellationToken).AsTask());
        }
        catch (OperationCanceledException exception)
        {
            CompleteCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            CompleteException(exception);
        }
    }

    protected override void SetCanceled(CancellationToken cancellationToken) =>
        _completion.TrySetCanceled(cancellationToken);

    protected override void SetException(Exception exception) =>
        _completion.TrySetException(exception);

    private async Task ObserveAsync(Task<T> operation)
    {
        try
        {
            var result = await operation.ConfigureAwait(false);
            if (TryCompleteStarted())
            {
                _completion.TrySetResult(result);
            }
        }
        catch (OperationCanceledException exception)
        {
            CompleteCanceled(exception.CancellationToken);
        }
        catch (Exception exception)
        {
            CompleteException(exception);
        }
    }

    private void CompleteCanceled(CancellationToken cancellationToken)
    {
        if (TryCompleteStarted())
        {
            _completion.TrySetCanceled(cancellationToken);
        }
    }

    private void CompleteException(Exception exception)
    {
        if (TryCompleteStarted())
        {
            _completion.TrySetException(exception);
        }
    }
}
