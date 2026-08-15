using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using VibeWallpaper.Engine.Native;

namespace VibeWallpaper.Engine.Runtime;

public sealed class EngineStaDispatcher : IEngineDispatcher
{
    private const int Accepting = 0;
    private const int Disposing = 1;
    private const int Disposed = 2;
    private const int MaximumWorkBatch = 8;
    internal const uint WorkMessage = User32.WmApp + 1;
    internal const uint ShutdownMessage = User32.WmApp + 2;
    internal const uint ForceShutdownMessage = User32.WmApp + 3;
    private static readonly TimeSpan DefaultShutdownTimeout = TimeSpan.FromSeconds(5);
    private static int s_threadSequence;

    private readonly object _lifecycleGate = new();
    private readonly ConcurrentQueue<EngineQueueEntry> _queue = new();
    private readonly ConcurrentQueue<EngineWorkItem> _cancellationControls = new();
    private readonly ConcurrentDictionary<EngineWorkItem, byte> _queuedWork = new();
    private readonly ConcurrentDictionary<EngineWorkItem, byte> _inFlight = new();
    private readonly EngineObservationTracker _observationTracker = new();
    private readonly Dictionary<uint, Action> _threadMessageHandlers = [];
    private readonly CancellationTokenSource _shutdownCancellation = new();
    private readonly CancellationToken _shutdownToken;
    private readonly SafeWaitHandle _wakeEvent;
    private readonly TaskCompletionSource<EngineStaDispatcher> _readiness = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _threadCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _disposeCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _shutdownCancellationCompletion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly EngineSynchronizationContext _synchronizationContext;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Thread _thread;
    private readonly IThreadMessagePoster _messagePoster;
    private readonly IThreadMessageReceiver _messageReceiver;
    private readonly Action? _disposalTransitionObserved;
    private Task? _disposeTask;
    private uint _nativeThreadId;
    private int _managedThreadId;
    private int _state;
    private int _wakePending;
    private int _quitRequested;
    private int _shutdownRequested;
    private int _forceShutdownRequested;
    private int _cooperativeDrainEnabled;
    private int _resourceDisposalScheduled;
    private int _shutdownCancellationInitiated;
    private long _cooperativeShutdownDeadline;
    private long _absoluteShutdownDeadline;
    private Exception? _lastPostingFailure;

    private readonly record struct EngineQueueEntry(
        Action Callback,
        EngineContinuationOwner? Owner);

    private EngineStaDispatcher(
        TimeSpan shutdownTimeout,
        string threadName,
        IThreadMessagePoster messagePoster,
        IThreadMessageReceiver messageReceiver,
        Action? disposalTransitionObserved)
    {
        if (shutdownTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(shutdownTimeout));
        }

        _shutdownTimeout = shutdownTimeout;
        _messagePoster = messagePoster;
        _messageReceiver = messageReceiver;
        _disposalTransitionObserved = disposalTransitionObserved;
        _shutdownToken = _shutdownCancellation.Token;
        _wakeEvent = Kernel32.CreateEvent(0, manualReset: true, initialState: false, name: null);
        if (_wakeEvent.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            _wakeEvent.Dispose();
            _shutdownCancellation.Dispose();
            throw new Win32Exception(error);
        }

        _synchronizationContext = new EngineSynchronizationContext(this);
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = threadName,
        };
        _thread.SetApartmentState(ApartmentState.STA);
    }

    public bool HasThreadAccess =>
        _managedThreadId != 0 && Environment.CurrentManagedThreadId == _managedThreadId;

    internal bool IsThreadAlive => _thread.IsAlive;
    internal string ThreadName => _thread.Name!;
    internal uint NativeThreadId => Volatile.Read(ref _nativeThreadId);
    internal int PendingObservationCount => _observationTracker.PendingCount;
    internal long CompletedObservationCount => _observationTracker.CompletedCount;
    internal Task ThreadCompletion => _threadCompletion.Task;
    internal Exception? LastPostingFailure => Volatile.Read(ref _lastPostingFailure);

    public static Task<EngineStaDispatcher> StartAsync() =>
        StartAsync(DefaultShutdownTimeout);

    internal static async Task<EngineStaDispatcher> StartAsync(
        TimeSpan shutdownTimeout,
        string? threadName = null,
        IThreadMessagePoster? messagePoster = null,
        IThreadMessageReceiver? messageReceiver = null,
        Action? disposalTransitionObserved = null)
    {
        var name = threadName ?? $"VibeWallpaper.Engine.STA.{Interlocked.Increment(ref s_threadSequence)}";
        var dispatcher = new EngineStaDispatcher(
            shutdownTimeout,
            name,
            messagePoster ?? NativeThreadMessagePoster.Instance,
            messageReceiver ?? NativeThreadMessageReceiver.Instance,
            disposalTransitionObserved);
        try
        {
            dispatcher._thread.Start();
        }
        catch
        {
            dispatcher._wakeEvent.Dispose();
            dispatcher._shutdownCancellation.Dispose();
            throw;
        }

        try
        {
            return await dispatcher._readiness.Task.ConfigureAwait(false);
        }
        catch
        {
            await dispatcher._threadCompletion.Task.ConfigureAwait(false);
            dispatcher.ScheduleResourceDisposal(Task.CompletedTask, waitForDisposal: false);
            throw;
        }
    }

    public Task InvokeAsync(
        Func<CancellationToken, ValueTask> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        return InvokeAsync(
            async token =>
            {
                await action(token).ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    public Task<T> InvokeAsync<T>(
        Func<CancellationToken, ValueTask<T>> action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<T>(cancellationToken);
        }

        var item = new EngineWorkItem<T>(action, cancellationToken);
        if (HasThreadAccess)
        {
            if (Volatile.Read(ref _state) != Accepting)
            {
                item.Fail(new ObjectDisposedException(nameof(EngineStaDispatcher)));
                return item.TypedCompletion;
            }

            StartWorkItem(item);
            return item.TypedCompletion;
        }

        lock (_lifecycleGate)
        {
            if (_state != Accepting)
            {
                item.Fail(new ObjectDisposedException(nameof(EngineStaDispatcher)));
                return item.TypedCompletion;
            }

            _queuedWork.TryAdd(item, 0);
            _queue.Enqueue(new EngineQueueEntry(() => StartWorkItem(item), Owner: null));
            var postingFailure = RequestWake();
            if (postingFailure is not null)
            {
                _queuedWork.TryRemove(item, out _);
                item.Fail(postingFailure);
            }
        }

        return item.TypedCompletion;
    }

    /// <summary>
    /// Stops the dispatcher from a non-engine thread. Calling this method from the engine STA
    /// is rejected because an engine work item cannot await its own shutdown.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        if (HasThreadAccess)
        {
            throw new InvalidOperationException(
                "DisposeAsync cannot be called from the engine thread; request shutdown from an external owner.");
        }

        var beginDispose = false;
        Task disposeTask;
        lock (_lifecycleGate)
        {
            if (_disposeTask is null)
            {
                var started = Stopwatch.GetTimestamp();
                _cooperativeShutdownDeadline = AddDuration(
                    started,
                    TimeSpan.FromTicks(Math.Max(1, _shutdownTimeout.Ticks / 2)));
                _absoluteShutdownDeadline = AddDuration(started, _shutdownTimeout);
                Interlocked.Exchange(ref _shutdownRequested, 1);
                Volatile.Write(ref _state, Disposing);
                _disposeTask = _disposeCompletion.Task;
                beginDispose = true;
            }

            disposeTask = _disposeTask;
        }

        if (beginDispose)
        {
            _disposalTransitionObserved?.Invoke();
            BeginDispose();
        }

        return new ValueTask(disposeTask);
    }

    internal void PostContinuation(EngineContinuationOwner? owner, Action continuation)
    {
        ArgumentNullException.ThrowIfNull(continuation);
        if (Volatile.Read(ref _quitRequested) != 0 || _threadCompletion.Task.IsCompleted)
        {
            return;
        }

        Exception? postingFailure;
        lock (_lifecycleGate)
        {
            if (Volatile.Read(ref _quitRequested) != 0 || _threadCompletion.Task.IsCompleted)
            {
                return;
            }

            var state = Volatile.Read(ref _state);
            if (state == Disposed)
            {
                throw new ObjectDisposedException(nameof(EngineStaDispatcher));
            }

            if (state != Accepting && (owner is null || !owner.IsAttached))
            {
                return;
            }

            _queue.Enqueue(new EngineQueueEntry(continuation, owner));
            postingFailure = RequestWake();
        }

        if (postingFailure is not null)
        {
            RecordPostingFailure(postingFailure);
            FailQueuedWork(postingFailure);
            foreach (var item in _inFlight.Keys)
            {
                item.Fail(postingFailure);
            }
        }
    }

    internal void SignalContinuationOwnerReady()
    {
        if (Volatile.Read(ref _forceShutdownRequested) != 0 || _threadCompletion.Task.IsCompleted)
        {
            return;
        }

        RecordPostingFailure(SignalWakeEvent());
    }

    private void QueueCancellationControl(EngineWorkItem item)
    {
        if (_threadCompletion.Task.IsCompleted)
        {
            return;
        }

        _cancellationControls.Enqueue(item);
        var postingFailure = RequestWake();
        if (postingFailure is not null)
        {
            RecordPostingFailure(postingFailure);
            item.Fail(postingFailure);
        }
    }

    internal Task RegisterThreadMessageHandlerAsync(uint message, Action handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (IsInternalMessage(message) || message < User32.WmApp)
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        return InvokeAsync(_ =>
        {
            _threadMessageHandlers[message] = handler;
            return ValueTask.CompletedTask;
        });
    }

    internal void PostThreadMessage(uint message)
    {
        if (IsInternalMessage(message) || message < User32.WmApp)
        {
            throw new ArgumentOutOfRangeException(nameof(message));
        }

        var postingFailure = PostNativeMessage(message);
        if (postingFailure is not null)
        {
            throw postingFailure;
        }
    }

    private void BeginDispose()
    {
        _ = CompleteDisposeAsync();
    }

    private async Task CompleteDisposeAsync()
    {
        try
        {
            CancelQueuedWork(_shutdownToken);
            RequestShutdown(force: false);
            if (_threadCompletion.Task.IsCompleted)
            {
                BeginShutdownCancellationAfterThreadExit();
            }

            ScheduleResourceDisposal(_shutdownCancellationCompletion.Task);
            EnableCooperativeDrain();
            await AwaitShutdownAsync().ConfigureAwait(false);
            await WaitForCancellationCallbacksUntilAsync(_absoluteShutdownDeadline).ConfigureAwait(false);
            _disposeCompletion.TrySetResult();
        }
        catch (Exception exception)
        {
            _disposeCompletion.TrySetException(exception);
        }
    }

    private async Task AwaitShutdownAsync()
    {
        await WaitForThreadUntilAsync(_cooperativeShutdownDeadline).ConfigureAwait(false);

        if (!_threadCompletion.Task.IsCompleted)
        {
            RequestShutdown(force: true);
        }

        await WaitForThreadUntilAsync(_absoluteShutdownDeadline).ConfigureAwait(false);

        if (!_threadCompletion.Task.IsCompleted)
        {
            var timeout = new TimeoutException(
                $"The engine thread did not terminate within the configured absolute shutdown bound " +
                $"(threadState={_thread.ThreadState}, inFlight={_inFlight.Count}, queued={_queuedWork.Count}, " +
                $"shutdown={Volatile.Read(ref _shutdownRequested)}, force={Volatile.Read(ref _forceShutdownRequested)}).",
                LastPostingFailure);
            FailQueuedWork(timeout);
            foreach (var item in _inFlight.Keys)
            {
                item.ForceCancel(_shutdownToken);
            }

            throw timeout;
        }

        await _threadCompletion.Task.ConfigureAwait(false);
        if (!_thread.Join(TimeSpan.FromMilliseconds(100)))
        {
            throw new InvalidOperationException("The completed engine thread did not terminate after its completion signal.");
        }

        Volatile.Write(ref _state, Disposed);
    }

    private void EnableCooperativeDrain()
    {
        Interlocked.Exchange(ref _cooperativeDrainEnabled, 1);
        RecordPostingFailure(SignalWakeEvent());
    }

    private void RequestShutdown(bool force)
    {
        lock (_lifecycleGate)
        {
            if (force)
            {
                Interlocked.Exchange(ref _forceShutdownRequested, 1);
            }

            Interlocked.Exchange(ref _shutdownRequested, 1);
        }

        var message = force ? ForceShutdownMessage : ShutdownMessage;
        var firstPostingFailure = PostNativeMessage(message);
        RecordPostingFailure(firstPostingFailure);
        var secondPostingFailure = PostNativeMessage(message);
        RecordPostingFailure(secondPostingFailure);

        var signalFailure = SignalWakeEvent();
        if (signalFailure is not null)
        {
            RecordPostingFailure(signalFailure);
            if (firstPostingFailure is not null && secondPostingFailure is not null)
            {
                throw new InvalidOperationException(
                    "Neither the engine wake event nor its native control message could be delivered.",
                    signalFailure);
            }
        }
    }

    private async Task WaitForThreadUntilAsync(long deadline)
    {
        var remaining = RemainingUntil(deadline);
        if (remaining > TimeSpan.Zero && !_threadCompletion.Task.IsCompleted)
        {
            await Task.WhenAny(_threadCompletion.Task, Task.Delay(remaining)).ConfigureAwait(false);
        }
    }

    private async Task WaitForCancellationCallbacksUntilAsync(long deadline)
    {
        var cancellationCompletion = _shutdownCancellationCompletion.Task;
        var remaining = RemainingUntil(deadline);
        if (!cancellationCompletion.IsCompleted && remaining > TimeSpan.Zero)
        {
            await Task.WhenAny(cancellationCompletion, Task.Delay(remaining)).ConfigureAwait(false);
        }
    }

    private void ThreadMain()
    {
        var comInitialized = false;
        Exception? fatalError = null;
        var previousContext = SynchronizationContext.Current;

        try
        {
            var result = Ole32.CoInitializeEx(
                0,
                Ole32.CoinitApartmentThreaded | Ole32.CoinitDisableOle1Dde);
            if (result < 0)
            {
                Marshal.ThrowExceptionForHR(result);
            }

            comInitialized = true;
            SynchronizationContext.SetSynchronizationContext(_synchronizationContext);
            _managedThreadId = Environment.CurrentManagedThreadId;
            _ = User32.PeekMessage(out _, 0, 0, 0, User32.PmNoRemove);
            Volatile.Write(ref _nativeThreadId, User32.GetCurrentThreadId());
            _readiness.TrySetResult(this);

            PumpMessages();
        }
        catch (Exception exception)
        {
            fatalError = exception;
            _readiness.TrySetException(exception);
        }
        finally
        {
            Interlocked.Exchange(ref _quitRequested, 1);
            if (Volatile.Read(ref _state) != Disposed)
            {
                Volatile.Write(ref _state, Disposing);
            }

            BeginShutdownCancellationAtBoundary();
            var terminalError = fatalError ?? new OperationCanceledException(_shutdownToken);
            FailQueuedWork(terminalError);
            foreach (var item in _inFlight.Keys)
            {
                if (fatalError is null)
                {
                    item.ForceCancel(_shutdownToken);
                }
                else
                {
                    item.Fail(fatalError);
                }
            }

            BeginShutdownCancellationAfterThreadExit();

            SynchronizationContext.SetSynchronizationContext(previousContext);
            if (comInitialized)
            {
                Ole32.CoUninitialize();
            }

            while (_queue.TryDequeue(out _))
            {
            }

            while (_cancellationControls.TryDequeue(out _))
            {
            }

            _observationTracker.ReleasePendingRegistry();
            lock (_lifecycleGate)
            {
                Volatile.Write(ref _nativeThreadId, 0);
            }

            Volatile.Write(ref _managedThreadId, 0);
            _threadCompletion.TrySetResult();
        }
    }

    private unsafe void PumpMessages()
    {
        var wakeHandle = _wakeEvent.DangerousGetHandle();
        while (true)
        {
            var result = User32.MsgWaitForMultipleObjectsEx(
                1,
                &wakeHandle,
                User32.Infinite,
                User32.QsAllInput,
                User32.MwmoInputAvailable);
            if (result == User32.WaitFailed)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (result == 0)
            {
                if (!Kernel32.ResetEvent(_wakeEvent))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
                }

                Interlocked.Exchange(ref _wakePending, 0);
                ProcessReliableWake();
                continue;
            }

            if (result != 1)
            {
                throw new InvalidOperationException($"Unexpected message wait result {result}.");
            }

            var receiveResult = _messageReceiver.TryReceive(out var message);
            if (receiveResult == ThreadMessageReceiveResult.Error)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }

            if (receiveResult == ThreadMessageReceiveResult.NoMessage)
            {
                continue;
            }

            if (receiveResult == ThreadMessageReceiveResult.Quit)
            {
                return;
            }

            if (message.Window == 0)
            {
                ProcessThreadMessage(message.Id);
                continue;
            }

            _ = User32.TranslateMessage(in message);
            _ = User32.DispatchMessage(in message);
        }
    }

    private void ProcessThreadMessage(uint message)
    {
        switch (message)
        {
            case WorkMessage:
                Interlocked.Exchange(ref _wakePending, 0);
                ProcessReliableWake();
                break;
            case ShutdownMessage:
                Interlocked.Exchange(ref _shutdownRequested, 1);
                ProcessReliableWake();
                break;
            case ForceShutdownMessage:
                Interlocked.Exchange(ref _shutdownRequested, 1);
                Interlocked.Exchange(ref _forceShutdownRequested, 1);
                ProcessReliableWake();
                break;
            default:
                if (_threadMessageHandlers.TryGetValue(message, out var handler))
                {
                    handler();
                }

                break;
        }
    }

    private void ProcessReliableWake()
    {
        if (Volatile.Read(ref _shutdownRequested) == 0)
        {
            BeginPendingCancellationControls();
            DrainQueueBatch(allowCooperativeShutdown: false);
            return;
        }

        BeginShutdownCancellationAtBoundary();
        CancelQueuedWork(_shutdownToken);
        if (Volatile.Read(ref _forceShutdownRequested) != 0)
        {
            foreach (var item in _inFlight.Keys)
            {
                item.ForceCancel(_shutdownToken);
            }

            RequestQuit();
            return;
        }

        if (Volatile.Read(ref _cooperativeDrainEnabled) != 0)
        {
            DrainQueueBatch(allowCooperativeShutdown: true);
        }

        if (_inFlight.IsEmpty)
        {
            RequestQuit();
        }
    }

    private List<Task> BeginPendingCancellationControls()
    {
        var cancellationTasks = new List<Task>();
        while (_cancellationControls.TryDequeue(out var item))
        {
            cancellationTasks.Add(item.BeginCooperativeCancellation());
        }

        return cancellationTasks;
    }

    private void BeginShutdownCancellationAtBoundary()
    {
        if (Interlocked.CompareExchange(ref _shutdownCancellationInitiated, 1, 0) != 0)
        {
            _ = BeginPendingCancellationControls();
            return;
        }

        var cancellationTasks = new List<Task>();
        foreach (var item in _inFlight.Keys)
        {
            cancellationTasks.Add(item.BeginCooperativeCancellation());
        }

        cancellationTasks.AddRange(BeginPendingCancellationControls());
        try
        {
            cancellationTasks.Add(_shutdownCancellation.CancelAsync());
        }
        catch (Exception exception)
        {
            cancellationTasks.Add(Task.FromException(exception));
        }

        TrackShutdownCancellation(Task.WhenAll(cancellationTasks));
    }

    private void BeginShutdownCancellationAfterThreadExit()
    {
        if (Interlocked.CompareExchange(ref _shutdownCancellationInitiated, 1, 0) != 0)
        {
            return;
        }

        Task cancellationCompletion;
        try
        {
            cancellationCompletion = _shutdownCancellation.CancelAsync();
        }
        catch (Exception exception)
        {
            cancellationCompletion = Task.FromException(exception);
        }

        TrackShutdownCancellation(cancellationCompletion);
    }

    private void TrackShutdownCancellation(Task cancellationCompletion) =>
        _ = CompleteShutdownCancellationAsync(
            cancellationCompletion,
            _shutdownCancellationCompletion);

    private static async Task CompleteShutdownCancellationAsync(
        Task cancellationCompletion,
        TaskCompletionSource completion)
    {
        try
        {
            await cancellationCompletion.ConfigureAwait(false);
            completion.TrySetResult();
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private void DrainQueueBatch(bool allowCooperativeShutdown)
    {
        var inspectionLimit = _queue.Count;
        var inspected = 0;
        var executed = 0;
        while (inspected < inspectionLimit && executed < MaximumWorkBatch)
        {
            BeginPendingCancellationControls();
            if (!CanDrainQueue(allowCooperativeShutdown) || !_queue.TryDequeue(out var entry))
            {
                break;
            }

            inspected++;
            var ownerCanRun = entry.Owner is null ||
                (allowCooperativeShutdown
                    ? entry.Owner.CanRunCooperatively
                    : entry.Owner.CanRunNormally);
            if (!ownerCanRun)
            {
                _queue.Enqueue(entry);
                continue;
            }

            if (!TryAdmitQueueExecution(allowCooperativeShutdown))
            {
                _queue.Enqueue(entry);
                break;
            }

            try
            {
                ExecuteQueueEntry(entry);
            }
            catch (Exception exception)
            {
                if (Volatile.Read(ref _state) == Accepting)
                {
                    _readiness.TrySetException(exception);
                }
            }

            executed++;
        }

        if (CanDrainQueue(allowCooperativeShutdown) &&
            executed == MaximumWorkBatch &&
            inspected < inspectionLimit)
        {
            var postingFailure = RequestWake();
            if (postingFailure is not null)
            {
                RecordPostingFailure(postingFailure);
                FailQueuedWork(postingFailure);
            }
        }
    }

    private static void ExecuteQueueEntry(EngineQueueEntry entry)
    {
        var previousContext = SynchronizationContext.Current;
        try
        {
            if (entry.Owner is not null)
            {
                SynchronizationContext.SetSynchronizationContext(entry.Owner.SynchronizationContext);
            }

            entry.Callback();
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private bool CanDrainQueue(bool allowCooperativeShutdown) =>
        (Volatile.Read(ref _state) == Accepting &&
         Volatile.Read(ref _shutdownRequested) == 0) ||
        (allowCooperativeShutdown &&
         Volatile.Read(ref _cooperativeDrainEnabled) != 0 &&
         Volatile.Read(ref _forceShutdownRequested) == 0);

    private bool TryAdmitQueueExecution(bool allowCooperativeShutdown)
    {
        // Callback admission and shutdown publication share this gate. Once admission wins,
        // the callback is the already-executing STA turn; if disposal/force wins, it cannot start.
        lock (_lifecycleGate)
        {
            return allowCooperativeShutdown
                ? _cooperativeDrainEnabled != 0 && _forceShutdownRequested == 0
                : _state == Accepting && _shutdownRequested == 0;
        }
    }

    private void StartWorkItem(EngineWorkItem item)
    {
        EngineContinuationOwner continuationOwner;
        lock (_lifecycleGate)
        {
            _queuedWork.TryRemove(item, out _);
            if (_state != Accepting)
            {
                item.CancelBeforeStart(_shutdownToken);
                return;
            }

            if (!item.TryBeginStart(_shutdownToken))
            {
                return;
            }

            continuationOwner = new EngineContinuationOwner(this);
            item.Prepare(continuationOwner, OnWorkItemTerminal, QueueCancellationControl);
            _inFlight.TryAdd(item, 0);
        }

        var previousContext = SynchronizationContext.Current;
        try
        {
            SynchronizationContext.SetSynchronizationContext(continuationOwner.SynchronizationContext);
            _observationTracker.Track(item.Run());
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
    }

    private void OnWorkItemTerminal(EngineWorkItem item)
    {
        _inFlight.TryRemove(item, out _);
        if (Volatile.Read(ref _state) == Disposing && _inFlight.IsEmpty)
        {
            if (HasThreadAccess)
            {
                RequestQuit();
            }
            else
            {
                var postingFailure = PostNativeMessage(ShutdownMessage);
                RecordPostingFailure(postingFailure);
                var signalFailure = SignalWakeEvent();
                RecordPostingFailure(signalFailure);
                if (postingFailure is not null && signalFailure is not null)
                {
                    FailQueuedWork(signalFailure);
                }
            }
        }
    }

    private void RequestQuit()
    {
        if (!HasThreadAccess)
        {
            throw new InvalidOperationException("WM_QUIT must be requested from the engine thread.");
        }

        if (Interlocked.Exchange(ref _quitRequested, 1) != 0)
        {
            return;
        }

        User32.PostQuitMessage(0);
    }

    private Exception? RequestWake()
    {
        var signalFailure = SignalWakeEvent();
        Exception? postingFailure = null;
        if (Interlocked.Exchange(ref _wakePending, 1) == 0)
        {
            postingFailure = PostNativeMessage(WorkMessage);
            if (postingFailure is not null)
            {
                RecordPostingFailure(postingFailure);
                Interlocked.Exchange(ref _wakePending, 0);
            }
        }

        if (signalFailure is not null)
        {
            RecordPostingFailure(signalFailure);
            return postingFailure is null ? null : signalFailure;
        }

        return null;
    }

    private Exception? SignalWakeEvent() =>
        Kernel32.SetEvent(_wakeEvent)
            ? null
            : new Win32Exception(Marshal.GetLastPInvokeError());

    private Exception? PostNativeMessage(uint message)
    {
        lock (_lifecycleGate)
        {
            var threadId = NativeThreadId;
            if (threadId == 0)
            {
                return new Win32Exception(Marshal.GetLastPInvokeError());
            }

            return _messagePoster.TryPost(threadId, message);
        }
    }

    private void RecordPostingFailure(Exception? exception)
    {
        if (exception is not null)
        {
            Interlocked.Exchange(ref _lastPostingFailure, exception);
        }
    }

    private void ScheduleResourceDisposal(Task cancellationCompletion, bool waitForDisposal = true)
    {
        if (Interlocked.Exchange(ref _resourceDisposalScheduled, 1) != 0)
        {
            return;
        }

        _ = DisposeResourcesAsync(
            cancellationCompletion,
            _threadCompletion.Task,
            waitForDisposal ? _disposeCompletion.Task : Task.CompletedTask,
            _wakeEvent,
            _shutdownCancellation);
    }

    private static async Task DisposeResourcesAsync(
        Task cancellationCompletion,
        Task threadCompletion,
        Task disposeCompletion,
        SafeWaitHandle wakeEvent,
        CancellationTokenSource shutdownCancellation)
    {
        await threadCompletion.ConfigureAwait(false);
        try
        {
            await disposeCompletion.ConfigureAwait(false);
        }
        catch
        {
            // A terminal disposal failure still permits resource cleanup after the thread exits.
        }

        wakeEvent.Dispose();
        try
        {
            await cancellationCompletion.ConfigureAwait(false);
        }
        catch
        {
            // Cancellation callbacks are user code. Their failure is observed here but does not
            // change the engine's bounded shutdown result.
        }

        shutdownCancellation.Dispose();
    }

    private static long AddDuration(long timestamp, TimeSpan duration)
    {
        var stopwatchTicks = Math.Max(1L, (long)Math.Ceiling(duration.TotalSeconds * Stopwatch.Frequency));
        return timestamp + stopwatchTicks;
    }

    private static TimeSpan RemainingUntil(long deadline)
    {
        var remainingTicks = deadline - Stopwatch.GetTimestamp();
        return remainingTicks <= 0
            ? TimeSpan.Zero
            : TimeSpan.FromSeconds(remainingTicks / (double)Stopwatch.Frequency);
    }

    private void CancelQueuedWork(CancellationToken cancellationToken)
    {
        foreach (var item in _queuedWork.Keys)
        {
            if (_queuedWork.TryRemove(item, out _))
            {
                item.CancelBeforeStart(cancellationToken);
            }
        }
    }

    private void FailQueuedWork(Exception exception)
    {
        foreach (var item in _queuedWork.Keys)
        {
            if (_queuedWork.TryRemove(item, out _))
            {
                item.Fail(exception);
            }
        }
    }

    private static bool IsInternalMessage(uint message) =>
        message is WorkMessage or ShutdownMessage or ForceShutdownMessage;
}
