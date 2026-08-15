using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using VibeWallpaper.Engine.Native;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Runtime;

public sealed class EngineStaDispatcherTests
{
    private static PendingOperationState? s_pendingRetentionState;
    private static BlockedCallbackRetentionState? s_blockedCallbackRetentionState;

    [Fact]
    public async Task HasThreadAccess_IsFalseForCallerAndTrueForEngineThread()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();

        Assert.False(dispatcher.HasThreadAccess);
        var hasThreadAccess = await dispatcher.InvokeAsync(
            _ => ValueTask.FromResult(dispatcher.HasThreadAccess),
            TestContext.Current.CancellationToken);

        Assert.True(hasThreadAccess);
    }

    [Fact]
    public async Task InvokeAsync_RejectsNullActions()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();

        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.InvokeAsync(
            (Func<CancellationToken, ValueTask>)null!,
            TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ArgumentNullException>(() => dispatcher.InvokeAsync<int>(
            null!,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task InvokeAsync_KeepsStaAffinityWhilePendingWorkDoesNotBlockNewCommands()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var calls = new List<(int Value, int ThreadId, ApartmentState Apartment)>();
        var firstStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var first = dispatcher.InvokeAsync(async ct =>
        {
            calls.Add((1, Environment.CurrentManagedThreadId,
                Thread.CurrentThread.GetApartmentState()));
            firstStarted.SetResult();
            await releaseFirst.Task.WaitAsync(ct);
            calls.Add((3, Environment.CurrentManagedThreadId,
                Thread.CurrentThread.GetApartmentState()));
        }, TestContext.Current.CancellationToken);

        await firstStarted.Task;
        var second = dispatcher.InvokeAsync(ct =>
        {
            calls.Add((2, Environment.CurrentManagedThreadId,
                Thread.CurrentThread.GetApartmentState()));
            releaseFirst.SetResult();
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(first, second);
        Assert.Equal([1, 2, 3], calls.Select(x => x.Value));
        Assert.Single(calls.Select(x => x.ThreadId).Distinct());
        Assert.All(calls, x => Assert.Equal(ApartmentState.STA, x.Apartment));
    }

    [Fact]
    public async Task InvokeAsync_MultipleAwaitsRetainOwnerAndStaAffinity()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var observations = new List<(bool HasAccess, ApartmentState Apartment)>();

        await dispatcher.InvokeAsync(async _ =>
        {
            observations.Add((dispatcher.HasThreadAccess, Thread.CurrentThread.GetApartmentState()));
            await Task.Yield();
            observations.Add((dispatcher.HasThreadAccess, Thread.CurrentThread.GetApartmentState()));
            await Task.Yield();
            observations.Add((dispatcher.HasThreadAccess, Thread.CurrentThread.GetApartmentState()));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(3, observations.Count);
        Assert.All(observations, observation =>
        {
            Assert.True(observation.HasAccess);
            Assert.Equal(ApartmentState.STA, observation.Apartment);
        });
    }

    [Fact]
    public async Task InvokeAsync_AlreadyCanceledTokenPreventsExecution()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var invoked = false;

        var task = dispatcher.InvokeAsync(_ =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        }, cancellation.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.False(invoked);
    }

    [Fact]
    public async Task InvokeAsync_CancellationAfterStartReachesDelegateAndCompletesTerminally()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var task = dispatcher.InvokeAsync(async ct =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                observed.SetResult();
                throw;
            }
        }, cancellation.Token);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await observed.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(task.IsCompleted);
    }

    [Fact]
    public async Task InvokeAsync_CancellationBeforeDequeuePreventsExecution()
    {
        const uint message = 0x8000 + 102;
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        using var releasePump = new ManualResetEventSlim();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var pumpPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;

        await dispatcher.RegisterThreadMessageHandlerAsync(message, () =>
        {
            pumpPaused.SetResult();
            if (!releasePump.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release the engine pump.");
            }
        });

        dispatcher.PostThreadMessage(message);
        await pumpPaused.Task.WaitAsync(TestContext.Current.CancellationToken);

        var queued = dispatcher.InvokeAsync(_ =>
        {
            invoked = true;
            return ValueTask.CompletedTask;
        }, cancellation.Token);
        cancellation.Cancel();

        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(invoked);
        }
        finally
        {
            releasePump.Set();
        }

        await dispatcher.InvokeAsync(
            _ => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);
        Assert.False(invoked);
    }

    [Fact]
    public async Task InvokeAsync_ExceptionPropagatesAndPumpRemainsUsable()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => dispatcher.InvokeAsync(ThrowSynchronously, TestContext.Current.CancellationToken));
        var result = await dispatcher.InvokeAsync(
            _ => ValueTask.FromResult(42),
            TestContext.Current.CancellationToken);

        Assert.Equal("injected", exception.Message);
        Assert.Equal(42, result);
    }

    [Fact]
    public async Task InvokeAsync_AsynchronousExceptionPropagatesAndPumpRemainsUsable()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();

        var exception = await Assert.ThrowsAsync<ApplicationException>(() =>
            dispatcher.InvokeAsync(async _ =>
            {
                await Task.Yield();
                throw new ApplicationException("asynchronous-injected");
            }, TestContext.Current.CancellationToken));
        var reachedLaterWork = await dispatcher.InvokeAsync(
            _ => ValueTask.FromResult(true),
            TestContext.Current.CancellationToken);

        Assert.Equal("asynchronous-injected", exception.Message);
        Assert.True(reachedLaterWork);
    }

    [Fact]
    public async Task InvokeAsync_NestedCallExecutesInlineWithoutDeadlock()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var calls = new List<int>();

        await dispatcher.InvokeAsync(async ct =>
        {
            calls.Add(1);
            await dispatcher.InvokeAsync(_ =>
            {
                calls.Add(2);
                return ValueTask.CompletedTask;
            }, ct);
            calls.Add(3);
        }, TestContext.Current.CancellationToken);

        Assert.Equal([1, 2, 3], calls);
    }

    [Fact]
    public async Task SynchronizationContext_SendRunsInlineOnStaAndRejectsCrossThreadSend()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        SynchronizationContext? engineContext = null;
        var inlineSendRan = false;

        await dispatcher.InvokeAsync(_ =>
        {
            engineContext = SynchronizationContext.Current;
            engineContext!.Send(_ => inlineSendRan = true, null);
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        Assert.True(inlineSendRan);
        Assert.IsType<EngineSynchronizationContext>(engineContext);
        Assert.Throws<NotSupportedException>(() => engineContext.Send(_ => { }, null));
    }

    [Fact]
    public async Task SynchronizationContext_CopyStillPostsToEngineSta()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        SynchronizationContext? copiedContext = null;
        var posted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.InvokeAsync(_ =>
        {
            copiedContext = SynchronizationContext.Current!.CreateCopy();
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        copiedContext!.Post(_ => posted.SetResult(dispatcher.HasThreadAccess), null);

        Assert.True(await posted.Task.WaitAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SynchronizationContext_DetachedPendingOwnerSignalsWhenCancellationCallbacksComplete()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(500));
        using var callerCancellation = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var releaseOperation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var posted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? copiedContext = null;
        CancellationTokenRegistration registration = default;

        var operation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            registration = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                try
                {
                    if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test did not release the cancellation callback.");
                    }
                }
                finally
                {
                    callbackExited.TrySetResult();
                }
            }, useSynchronizationContext: false);
            copiedContext = SynchronizationContext.Current!.CreateCopy();
            started.TrySetResult();
            await releaseOperation.Task.ConfigureAwait(false);
        }, callerCancellation.Token);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var callerCancellationCompletion = callerCancellation.CancelAsync();
            await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            copiedContext!.Post(
                _ => posted.TrySetResult(dispatcher.HasThreadAccess),
                null);
            var independent = dispatcher.InvokeAsync(
                _ => ValueTask.FromResult(dispatcher.HasThreadAccess),
                CancellationToken.None);
            Assert.True(await independent.WaitAsync(TestContext.Current.CancellationToken));
            Assert.False(posted.Task.IsCompleted);

            releaseOperation.TrySetResult();
            await operation.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(posted.Task.IsCompleted);

            releaseCallback.Set();
            await callbackExited.Task.WaitAsync(TestContext.Current.CancellationToken);
            await callerCancellationCompletion.WaitAsync(TestContext.Current.CancellationToken);
            await registration.DisposeAsync();

            Assert.True(await posted.Task.WaitAsync(
                TimeSpan.FromMilliseconds(300),
                TestContext.Current.CancellationToken));
        }
        finally
        {
            releaseOperation.TrySetResult();
            releaseCallback.Set();
            await registration.DisposeAsync();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task SynchronizationContext_DetachedCopyPostAfterDisposalLinearizationNeverExecutes()
    {
        SynchronizationContext? copiedContext = null;
        using var callbackRan = new ManualResetEventSlim();
        var boundaryObserved = false;
        var callbackRanDuringBoundary = false;
        Exception? postError = null;
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(500),
            disposalTransitionObserved: () =>
            {
                boundaryObserved = true;
                try
                {
                    copiedContext!.Post(_ => callbackRan.Set(), null);
                    callbackRanDuringBoundary = callbackRan.Wait(TimeSpan.FromMilliseconds(250));
                }
                catch (Exception exception)
                {
                    postError = exception;
                }
            });

        await dispatcher.InvokeAsync(_ =>
        {
            copiedContext = SynchronizationContext.Current!.CreateCopy();
            return ValueTask.CompletedTask;
        }, TestContext.Current.CancellationToken);

        await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(boundaryObserved);
        Assert.False(callbackRanDuringBoundary);
        Assert.False(callbackRan.IsSet);
        Assert.True(postError is null or ObjectDisposedException);
    }

    [Fact]
    public async Task InvokeAsync_ManyQueuedItemsPreserveFifoStartOrder()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var starts = new List<int>();

        var tasks = Enumerable.Range(0, 256)
            .Select(index => dispatcher.InvokeAsync(
                _ =>
                {
                    starts.Add(index);
                    return ValueTask.CompletedTask;
                },
                TestContext.Current.CancellationToken))
            .ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(Enumerable.Range(0, 256), starts);
    }

    [Fact]
    public async Task NativeThreadMessage_IsHandledWhileDelegateAwaits()
    {
        const uint message = 0x8000 + 100;
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var calls = new List<(int Value, int ThreadId, ApartmentState Apartment)>();
        var delegateStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseDelegate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await dispatcher.RegisterThreadMessageHandlerAsync(message, () =>
        {
            calls.Add((2, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
            messageHandled.SetResult();
        });

        var operation = dispatcher.InvokeAsync(async ct =>
        {
            calls.Add((1, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
            delegateStarted.SetResult();
            await releaseDelegate.Task.WaitAsync(ct);
            calls.Add((3, Environment.CurrentManagedThreadId, Thread.CurrentThread.GetApartmentState()));
        }, TestContext.Current.CancellationToken);

        try
        {
            await delegateStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            Assert.NotEqual(0u, dispatcher.NativeThreadId);
            dispatcher.PostThreadMessage(message);
            await messageHandled.Task.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal([1, 2], calls.Select(call => call.Value));
        }
        finally
        {
            releaseDelegate.TrySetResult();
        }

        await operation;
        Assert.Equal([1, 2, 3], calls.Select(call => call.Value));
        Assert.Single(calls.Select(call => call.ThreadId).Distinct());
        Assert.All(calls, call => Assert.Equal(ApartmentState.STA, call.Apartment));
    }

    [Fact]
    public async Task NativeThreadMessage_IsNotStarvedByYieldChurn()
    {
        const uint message = 0x8000 + 103;
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var messageHandled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopChurn = 0;

        await dispatcher.RegisterThreadMessageHandlerAsync(message, messageHandled.SetResult);
        var churn = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            while (Volatile.Read(ref stopChurn) == 0)
            {
                await Task.Yield();
            }
        }, TestContext.Current.CancellationToken);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        dispatcher.PostThreadMessage(message);

        try
        {
            var completed = await Task.WhenAny(
                messageHandled.Task,
                Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));
            Assert.Same(messageHandled.Task, completed);
        }
        finally
        {
            Interlocked.Exchange(ref stopChurn, 1);
            await churn.WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DisposeAsync_YieldChurnCannotStarveBoundedForcedShutdown()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopChurn = 0;
        var churn = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            while (Volatile.Read(ref stopChurn) == 0)
            {
                await Task.Yield();
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var disposal = dispatcher.DisposeAsync().AsTask();
            var completed = await Task.WhenAny(
                disposal,
                Task.Delay(TimeSpan.FromMilliseconds(750), TestContext.Current.CancellationToken));

            Assert.Same(disposal, completed);
            Assert.True(churn.IsCompleted);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            Interlocked.Exchange(ref stopChurn, 1);
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DisposeAsync_RejectsNewWorkAndIsIdempotent()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync();
        try
        {
            await dispatcher.DisposeAsync();
            await dispatcher.DisposeAsync();

            await Assert.ThrowsAsync<ObjectDisposedException>(
                () => dispatcher.InvokeAsync(
                    _ => ValueTask.CompletedTask,
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_ReentrantCancellationCallbackReturnsSamePublishedTask()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(500));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? reentrantDisposal = null;
        var operation = dispatcher.InvokeAsync(async ct =>
        {
            using var registration = ct.Register(() => reentrantDisposal = dispatcher.DisposeAsync().AsTask());
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var disposalCall = Task.Factory.StartNew(
                () => dispatcher.DisposeAsync().AsTask(),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            var publishedDisposal = await disposalCall.WaitAsync(TestContext.Current.CancellationToken);
            await publishedDisposal.WaitAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(reentrantDisposal);
            Assert.Same(publishedDisposal, reentrantDisposal);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_OnEngineThreadIsRejectedImmediately()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();

        var exception = await dispatcher.InvokeAsync(cancellationToken =>
        {
            try
            {
                _ = dispatcher.DisposeAsync();
                return ValueTask.FromResult<Exception?>(null);
            }
            catch (Exception caught)
            {
                return ValueTask.FromResult<Exception?>(caught);
            }
        }, TestContext.Current.CancellationToken);

        var invalidOperation = Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("engine thread", invalidOperation.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DisposeAsync_CancellationCallbackDoesNotHoldLifecycleLockAgainstOtherCallers()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(500));
        using var releaseCallback = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async ct =>
        {
            using var registration = ct.Register(() =>
            {
                callbackEntered.SetResult();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test did not release the cancellation callback.");
                }
            });
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, TestContext.Current.CancellationToken);

        Task<Task>? firstCall = null;
        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            firstCall = Task.Factory.StartNew(
                () => dispatcher.DisposeAsync().AsTask(),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            var secondCall = Task.Factory.StartNew(
                () => dispatcher.DisposeAsync().AsTask(),
                TestContext.Current.CancellationToken,
                TaskCreationOptions.DenyChildAttach,
                TaskScheduler.Default);
            var secondPublished = await secondCall.WaitAsync(
                TimeSpan.FromMilliseconds(200),
                TestContext.Current.CancellationToken);

            releaseCallback.Set();
            var firstPublished = await firstCall.WaitAsync(TestContext.Current.CancellationToken);
            Assert.Same(firstPublished, secondPublished);
            await firstPublished.WaitAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        finally
        {
            releaseCallback.Set();
            if (firstCall is not null)
            {
                _ = await firstCall.WaitAsync(TestContext.Current.CancellationToken);
            }

            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_ReturnsImmediatelyAndRemainsBoundedWhileCancellationCallbackBlocks()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        using var releaseCallback = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async ct =>
        {
            using var registration = ct.Register(() =>
            {
                callbackEntered.SetResult();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test did not release the cancellation callback.");
                }
            });
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, TestContext.Current.CancellationToken);
        var fallbackRelease = Task.Delay(
                TimeSpan.FromMilliseconds(300),
                TestContext.Current.CancellationToken)
            .ContinueWith(
                _ => releaseCallback.Set(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var disposal = dispatcher.DisposeAsync().AsTask();
            stopwatch.Stop();

            Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(100), $"DisposeAsync returned after {stopwatch.Elapsed}.");
            await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
            await disposal.WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            Assert.False(releaseCallback.IsSet);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            releaseCallback.Set();
            await fallbackRelease.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_CallerCancellationRaceCannotBlockStaForce(
        bool callerCancellationStartsFirst)
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        using var callerCancellation = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test did not release the caller cancellation callback.");
                }
            }, useSynchronizationContext: false);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, callerCancellation.Token);
        using var safetyTimer = new Timer(
            _ => releaseCallback.Set(),
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            Task callerCancellationCompletion;
            Task disposal;
            if (callerCancellationStartsFirst)
            {
                callerCancellationCompletion = callerCancellation.CancelAsync();
                await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.False(releaseCallback.IsSet);
                disposal = dispatcher.DisposeAsync().AsTask();
            }
            else
            {
                disposal = dispatcher.DisposeAsync().AsTask();
                await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
                Assert.False(releaseCallback.IsSet);
                callerCancellationCompletion = callerCancellation.CancelAsync();
            }

            await disposal.WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
            Assert.False(releaseCallback.IsSet);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);

            releaseCallback.Set();
            await callerCancellationCompletion.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseCallback.Set();
            await dispatcher.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancellationInitiation_WaitsForExecutingOwnerBoundaryAndPreservesOtherCooperativeWork(
        bool callerCancellationStartsFirst)
    {
        var poster = new ScriptedThreadMessagePoster(static (_, _) => false);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(800),
            messagePoster: poster);
        using var callerCancellation = new CancellationTokenSource();
        using var releaseExecutingContinuation = new ManualResetEventSlim();
        using var releaseBlockingCallback = new ManualResetEventSlim();
        var enterExecutingContinuation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerContinuationEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackExited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var yieldedOwnerContinuationRan = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cooperativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cooperativeFinally = new TaskCompletionSource<(bool HasAccess, ApartmentState Apartment)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var ownerOperation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                try
                {
                    if (!releaseBlockingCallback.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test did not release the blocking cancellation callback.");
                    }
                }
                finally
                {
                    callbackExited.TrySetResult();
                }
            }, useSynchronizationContext: false);
            ownerStarted.TrySetResult();
            await enterExecutingContinuation.Task;
            ownerContinuationEntered.TrySetResult();
            if (!releaseExecutingContinuation.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release the executing owner continuation.");
            }

            await Task.Yield();
            yieldedOwnerContinuationRan.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, callerCancellation.Token);
        var cooperativeOperation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            cooperativeStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cooperativeFinally.TrySetResult((
                    dispatcher.HasThreadAccess,
                    Thread.CurrentThread.GetApartmentState()));
            }
        }, CancellationToken.None);
        Task? callerCancellationCompletion = null;
        Task? disposal = null;
        using var safetyTimer = new Timer(
            _ =>
            {
                releaseExecutingContinuation.Set();
                releaseBlockingCallback.Set();
            },
            null,
            TimeSpan.FromSeconds(3),
            Timeout.InfiniteTimeSpan);

        try
        {
            await Task.WhenAll(ownerStarted.Task, cooperativeStarted.Task)
                .WaitAsync(TestContext.Current.CancellationToken);
            enterExecutingContinuation.TrySetResult();
            await ownerContinuationEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            if (callerCancellationStartsFirst)
            {
                callerCancellationCompletion = callerCancellation.CancelAsync();
            }
            else
            {
                disposal = dispatcher.DisposeAsync().AsTask();
            }

            var boundaryWindow = Task.Delay(
                TimeSpan.FromMilliseconds(125),
                TestContext.Current.CancellationToken);
            var firstAtBoundary = await Task.WhenAny(callbackEntered.Task, boundaryWindow);
            Assert.Same(boundaryWindow, firstAtBoundary);
            Assert.False(callbackEntered.Task.IsCompleted);

            if (callerCancellationStartsFirst)
            {
                disposal = dispatcher.DisposeAsync().AsTask();
            }
            else
            {
                callerCancellationCompletion = callerCancellation.CancelAsync();
            }

            releaseExecutingContinuation.Set();
            await callbackEntered.Task.WaitAsync(
                TimeSpan.FromMilliseconds(300),
                TestContext.Current.CancellationToken);
            var finallyThread = await cooperativeFinally.Task.WaitAsync(
                TimeSpan.FromMilliseconds(300),
                TestContext.Current.CancellationToken);

            Assert.True(finallyThread.HasAccess);
            Assert.Equal(ApartmentState.STA, finallyThread.Apartment);
            Assert.False(yieldedOwnerContinuationRan.Task.IsCompleted);
            Assert.False(releaseBlockingCallback.IsSet);

            await disposal!.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ownerOperation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cooperativeOperation);
            Assert.False(dispatcher.IsThreadAlive);

            releaseBlockingCallback.Set();
            await callbackExited.Task.WaitAsync(TestContext.Current.CancellationToken);
            await callerCancellationCompletion!.WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            releaseExecutingContinuation.Set();
            releaseBlockingCallback.Set();
            if (callerCancellationCompletion is not null)
            {
                await callerCancellationCompletion.WaitAsync(TestContext.Current.CancellationToken);
            }

            disposal ??= dispatcher.DisposeAsync().AsTask();
            await disposal.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task CallerCancellation_BlockedOwnerDoesNotSuppressIndependentCommandBeforeShutdown()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(500));
        using var callerCancellation = new CancellationTokenSource();
        using var releaseCallback = new ManualResetEventSlim();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(
                () =>
                {
                    callbackEntered.TrySetResult();
                    if (!releaseCallback.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test did not release the caller cancellation callback.");
                    }
                },
                useSynchronizationContext: false);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, callerCancellation.Token);

        Task? callerCancellationCompletion = null;
        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            callerCancellationCompletion = callerCancellation.CancelAsync();
            await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            var independent = dispatcher.InvokeAsync(
                _ => ValueTask.FromResult((
                    dispatcher.HasThreadAccess,
                    Thread.CurrentThread.GetApartmentState())),
                CancellationToken.None);
            var completed = await Task.WhenAny(
                independent,
                Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));

            Assert.Same(independent, completed);
            Assert.False(releaseCallback.IsSet);
            var snapshot = await independent;
            Assert.True(snapshot.HasThreadAccess);
            Assert.Equal(ApartmentState.STA, snapshot.Item2);
        }
        finally
        {
            releaseCallback.Set();
            if (callerCancellationCompletion is not null)
            {
                await callerCancellationCompletion.WaitAsync(TestContext.Current.CancellationToken);
            }

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            await dispatcher.DisposeAsync();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisposeAsync_BlockedCallbackDoesNotSuppressOtherCooperativeFinally(
        bool blockedOperationStartsFirst)
    {
        var poster = new ScriptedThreadMessagePoster(static (_, _) => false);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(400),
            messagePoster: poster);
        using var releaseBlockedCallback = new ManualResetEventSlim();
        using var otherCancellationObserved = new ManualResetEventSlim();
        var blockedStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherFinally = new TaskCompletionSource<(bool HasAccess, ApartmentState Apartment)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackHadStaAccess = true;
        var safetyTimer = new Timer(
            _ => releaseBlockedCallback.Set(),
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan);

        Task StartBlockedOperation() => dispatcher.InvokeAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() =>
            {
                callbackHadStaAccess = dispatcher.HasThreadAccess;
                callbackEntered.TrySetResult();
                if (!otherCancellationObserved.Wait(TimeSpan.FromSeconds(1)))
                {
                    throw new TimeoutException("The other operation was not canceled before the blocking callback.");
                }

                if (!releaseBlockedCallback.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test did not release the blocking cancellation callback.");
                }
            }, useSynchronizationContext: false);
            blockedStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
        }, TestContext.Current.CancellationToken);

        Task StartOtherOperation() => dispatcher.InvokeAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(
                otherCancellationObserved.Set,
                useSynchronizationContext: false);
            otherStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                otherFinally.TrySetResult((
                    dispatcher.HasThreadAccess,
                    Thread.CurrentThread.GetApartmentState()));
            }
        }, TestContext.Current.CancellationToken);

        Task blockedOperation;
        Task otherOperation;
        if (blockedOperationStartsFirst)
        {
            blockedOperation = StartBlockedOperation();
            await blockedStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            otherOperation = StartOtherOperation();
        }
        else
        {
            otherOperation = StartOtherOperation();
            await otherStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            blockedOperation = StartBlockedOperation();
        }

        try
        {
            await Task.WhenAll(blockedStarted.Task, otherStarted.Task)
                .WaitAsync(TestContext.Current.CancellationToken);
            var disposal = dispatcher.DisposeAsync().AsTask();
            await callbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);

            var cooperativeResult = await Task.WhenAny(
                otherFinally.Task,
                Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));

            Assert.Same(otherFinally.Task, cooperativeResult);
            Assert.Equal(0, poster.AttemptCount(EngineStaDispatcher.ForceShutdownMessage));
            Assert.False(callbackHadStaAccess);
            Assert.False(releaseBlockedCallback.IsSet);

            var finallyThread = await otherFinally.Task;
            Assert.True(finallyThread.HasAccess);
            Assert.Equal(ApartmentState.STA, finallyThread.Apartment);

            await disposal.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.False(releaseBlockedCallback.IsSet);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blockedOperation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => otherOperation);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            releaseBlockedCallback.Set();
            await safetyTimer.DisposeAsync();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_TwoBlockedCallbacksDoNotSuppressIndependentCooperativeFinally()
    {
        var poster = new ScriptedThreadMessagePoster(static (_, _) => false);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(500),
            messagePoster: poster);
        using var releaseFirst = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cooperativeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondCallbackEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cooperativeFinally = new TaskCompletionSource<(bool HasAccess, ApartmentState Apartment)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var safetyTimer = new Timer(
            _ =>
            {
                releaseFirst.Set();
                releaseSecond.Set();
            },
            null,
            TimeSpan.FromSeconds(2),
            Timeout.InfiniteTimeSpan);

        Task StartBlockedOperation(
            TaskCompletionSource started,
            TaskCompletionSource callbackEntered,
            ManualResetEventSlim release) => dispatcher.InvokeAsync(async cancellationToken =>
        {
            using var registration = cancellationToken.Register(() =>
            {
                callbackEntered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(5)))
                {
                    throw new TimeoutException("Test did not release a blocking cancellation callback.");
                }
            }, useSynchronizationContext: false);
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }, TestContext.Current.CancellationToken);

        var firstOperation = StartBlockedOperation(firstStarted, firstCallbackEntered, releaseFirst);
        await firstStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var cooperativeOperation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            cooperativeStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                cooperativeFinally.TrySetResult((
                    dispatcher.HasThreadAccess,
                    Thread.CurrentThread.GetApartmentState()));
            }
        }, TestContext.Current.CancellationToken);
        await cooperativeStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var secondOperation = StartBlockedOperation(secondStarted, secondCallbackEntered, releaseSecond);

        try
        {
            await secondStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
            var disposal = dispatcher.DisposeAsync().AsTask();
            await Task.WhenAll(firstCallbackEntered.Task, secondCallbackEntered.Task)
                .WaitAsync(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken);

            var cooperativeResult = await Task.WhenAny(
                cooperativeFinally.Task,
                Task.Delay(TimeSpan.FromMilliseconds(150), TestContext.Current.CancellationToken));

            Assert.Same(cooperativeFinally.Task, cooperativeResult);
            Assert.Equal(0, poster.AttemptCount(EngineStaDispatcher.ForceShutdownMessage));
            Assert.False(releaseFirst.IsSet);
            Assert.False(releaseSecond.IsSet);
            var finallyThread = await cooperativeFinally.Task;
            Assert.True(finallyThread.HasAccess);
            Assert.Equal(ApartmentState.STA, finallyThread.Apartment);

            await disposal.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.False(releaseFirst.IsSet);
            Assert.False(releaseSecond.IsSet);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstOperation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cooperativeOperation);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => secondOperation);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            releaseFirst.Set();
            releaseSecond.Set();
            await safetyTimer.DisposeAsync();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsCooperativeInFlightWorkAndTerminatesThread()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromSeconds(1));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async ct =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);
            await dispatcher.DisposeAsync();
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_CooperativeCancellationRunsFinallyOnStaBeforeShutdown()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(500));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finallyThread = new TaskCompletionSource<(bool HasAccess, ApartmentState Apartment)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            started.SetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                finallyThread.TrySetResult((
                    dispatcher.HasThreadAccess,
                    Thread.CurrentThread.GetApartmentState()));
            }
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(
                finallyThread.Task.IsCompleted,
                "Cooperative finally did not run before disposal completed.");
            var snapshot = await finallyThread.Task;
            Assert.True(snapshot.HasAccess);
            Assert.Equal(ApartmentState.STA, snapshot.Apartment);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_NonCooperativeWorkKeepsForceOutOfCooperativeWindow()
    {
        var poster = new ScriptedThreadMessagePoster(static (_, _) => false);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(300),
            messagePoster: poster);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            await neverCompletes.Task;
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var disposal = dispatcher.DisposeAsync().AsTask();

            Assert.Equal(0, poster.AttemptCount(EngineStaDispatcher.ForceShutdownMessage));
            Assert.False(disposal.IsCompleted);

            await disposal.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.True(poster.AttemptCount(EngineStaDispatcher.ForceShutdownMessage) > 0);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            neverCompletes.TrySetResult();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_CancelsQueuedWorkBeforeItCanStart()
    {
        const uint message = 0x8000 + 101;
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromSeconds(1));
        using var releasePump = new ManualResetEventSlim();
        var pumpPaused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoked = false;

        await dispatcher.RegisterThreadMessageHandlerAsync(message, () =>
        {
            pumpPaused.SetResult();
            if (!releasePump.Wait(TimeSpan.FromSeconds(5)))
            {
                throw new TimeoutException("Test did not release the engine pump.");
            }
        });

        dispatcher.PostThreadMessage(message);
        await pumpPaused.Task.WaitAsync(TestContext.Current.CancellationToken);

        try
        {
            var queued = dispatcher.InvokeAsync(_ =>
            {
                invoked = true;
                return ValueTask.CompletedTask;
            }, TestContext.Current.CancellationToken);
            var disposal = dispatcher.DisposeAsync().AsTask();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(invoked);
            releasePump.Set();
            await disposal.WaitAsync(TestContext.Current.CancellationToken);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            releasePump.Set();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_NonCooperativeAsyncWorkCannotExceedBoundOrStrandCaller()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            await neverCompletes.Task;
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

            stopwatch.Stop();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1), $"Shutdown took {stopwatch.Elapsed}.");
            Assert.True(operation.IsCompleted);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_LateFaultIsObservedAfterForcedCallerCompletion()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateResult = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(_ =>
        {
            started.SetResult();
            return new ValueTask<int>(lateResult.Task);
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var completedObservations = dispatcher.CompletedObservationCount;
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.Equal(0, dispatcher.PendingObservationCount);

            lateResult.SetException(new InvalidOperationException("late-observed"));
            for (var attempt = 0;
                 attempt < 50 && dispatcher.CompletedObservationCount == completedObservations;
                 attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }

            Assert.True(dispatcher.CompletedObservationCount > completedObservations);
            Assert.Equal(0, dispatcher.PendingObservationCount);
        }
        finally
        {
            lateResult.TrySetResult(0);
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_LateReleaseCannotRunBodyContinuationAfterThreadExit()
    {
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyContinuations = 0;
        var operation = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            await release.Task;
            Interlocked.Increment(ref bodyContinuations);
            throw new InvalidOperationException("late-body");
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);

            release.SetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

            Assert.Equal(0, Volatile.Read(ref bodyContinuations));
            Assert.Equal(0, dispatcher.PendingObservationCount);
        }
        finally
        {
            release.TrySetResult();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task ForcedPendingObservationDoesNotRetainDispatcher()
    {
        var fixture = await CreateForcedPendingDispatcherReferenceAsync();
        try
        {
            for (var attempt = 0; attempt < 20 && IsTargetAlive(fixture.Dispatcher); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }

            Assert.False(IsTargetAlive(fixture.Dispatcher));
        }
        finally
        {
            fixture.State.Release.TrySetResult();
            await Task.Delay(TimeSpan.FromMilliseconds(50), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task BlockedCancellationCallbackDoesNotRetainDispatcherAfterForcedShutdown()
    {
        var fixture = await CreateBlockedCallbackRetentionFixtureAsync();
        try
        {
            for (var attempt = 0; attempt < 20 && IsTargetAlive(fixture.Dispatcher); attempt++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                await Task.Delay(TimeSpan.FromMilliseconds(20), TestContext.Current.CancellationToken);
            }

            Assert.False(IsTargetAlive(fixture.Dispatcher));
            Assert.False(fixture.State.ReleaseCallback.IsSet);
        }
        finally
        {
            fixture.State.ReleaseCallback.Set();
            await fixture.State.CallbackExited.Task.WaitAsync(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task DisposeAsync_RetriesInitialShutdownPostFailure()
    {
        var poster = new ScriptedThreadMessagePoster(
            static (message, attempt) =>
                message == EngineStaDispatcher.ShutdownMessage && attempt == 1);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(500),
            messagePoster: poster);

        await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

        Assert.True(poster.AttemptCount(EngineStaDispatcher.ShutdownMessage) >= 2);
        Assert.False(dispatcher.IsThreadAlive);
    }

    [Fact]
    public async Task DisposeAsync_RetriesForcePostFailureWithinBound()
    {
        var poster = new ScriptedThreadMessagePoster(
            static (message, attempt) =>
                message == EngineStaDispatcher.ForceShutdownMessage && attempt == 1);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(300),
            messagePoster: poster);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            await neverCompletes.Task;
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(poster.AttemptCount(EngineStaDispatcher.ForceShutdownMessage) >= 2);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            neverCompletes.TrySetResult();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_PermanentPostFailureFaultsTerminallyWithinAbsoluteBound()
    {
        var poster = new ScriptedThreadMessagePoster(
            static (message, _) =>
                message is EngineStaDispatcher.ShutdownMessage or EngineStaDispatcher.ForceShutdownMessage);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(200),
            messagePoster: poster);
        var disposal = dispatcher.DisposeAsync().AsTask();
        await disposal.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.True(disposal.IsCompletedSuccessfully);
        Assert.False(dispatcher.IsThreadAlive);
        Assert.True(poster.AttemptCount(EngineStaDispatcher.ShutdownMessage) > 0);
    }

    [Fact]
    public async Task InvokeAsync_WorkMessagePostFailureAfterAwaitUsesReliableWake()
    {
        var poster = new ScriptedThreadMessagePoster(
            static (message, attempt) =>
                message == EngineStaDispatcher.WorkMessage && attempt == 2);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(300),
            messagePoster: poster);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var operation = dispatcher.InvokeAsync(async _ =>
            {
                started.SetResult();
                await release.Task;
                return 73;
            }, TestContext.Current.CancellationToken);
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);

            release.SetResult();
            var result = await operation.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);

            Assert.Equal(73, result);
            Assert.NotNull(dispatcher.LastPostingFailure);
            Assert.True(poster.AttemptCount(EngineStaDispatcher.WorkMessage) >= 2);
        }
        finally
        {
            release.TrySetResult();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task PumpFatalAfterReadinessFaultsPendingWorkAndLaterDisposeIsClean()
    {
        const uint fatalMessage = 0x8000 + 105;
        var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(200));
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await dispatcher.RegisterThreadMessageHandlerAsync(
            fatalMessage,
            static () => throw new InvalidOperationException("fatal-pump"));
        var operation = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            await neverCompletes.Task;
        }, TestContext.Current.CancellationToken);

        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        dispatcher.PostThreadMessage(fatalMessage);
        await dispatcher.ThreadCompletion.WaitAsync(TestContext.Current.CancellationToken);

        var operationError = await Assert.ThrowsAsync<InvalidOperationException>(() => operation);
        Assert.Equal("fatal-pump", operationError.Message);
        Assert.False(dispatcher.IsThreadAlive);

        var firstDisposal = dispatcher.DisposeAsync().AsTask();
        var secondDisposal = dispatcher.DisposeAsync().AsTask();
        Assert.Same(firstDisposal, secondDisposal);
        await firstDisposal.WaitAsync(TestContext.Current.CancellationToken);
        await dispatcher.DisposeAsync();
    }

    [Fact]
    public async Task ReceiveFailureAfterReadinessFaultsPendingWorkAndLaterDisposeIsClean()
    {
        const uint triggerMessage = 0x8000 + 106;
        var receiver = new SwitchableThreadMessageReceiver();
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(200),
            messageReceiver: receiver);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var neverCompletes = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            await neverCompletes.Task.ConfigureAwait(false);
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            receiver.FailNextReceive();
            dispatcher.PostThreadMessage(triggerMessage);
            await dispatcher.ThreadCompletion.WaitAsync(TestContext.Current.CancellationToken);

            await Assert.ThrowsAsync<Win32Exception>(() => operation);
            Assert.False(dispatcher.IsThreadAlive);
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            neverCompletes.TrySetResult();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReceiveFailure_CancelsInFlightActionTokenBeforeTerminalCleanup()
    {
        const uint triggerMessage = 0x8000 + 110;
        var receiver = new SwitchableThreadMessageReceiver();
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(300),
            messageReceiver: receiver);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionObservedCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        CancellationToken suppliedToken = default;
        var operation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            suppliedToken = cancellationToken;
            registration = cancellationToken.Register(
                () => callbackRan.TrySetResult(),
                useSynchronizationContext: false);
            started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                actionObservedCancellation.TrySetResult();
                throw;
            }
        }, CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            receiver.FailNextReceive();
            dispatcher.PostThreadMessage(triggerMessage);
            await dispatcher.ThreadCompletion.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(suppliedToken.IsCancellationRequested);
            await callbackRan.Task.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);
            await actionObservedCancellation.Task.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);
            var terminalError = await Record.ExceptionAsync(() => operation);
            Assert.True(terminalError is Win32Exception or OperationCanceledException);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await operation;
            }
            catch
            {
                // The injected fatal receive may fault or cancel the caller task.
            }

            await registration.DisposeAsync();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pump_MessageInputFalsePositiveDoesNotBlockEventOnlyWake()
    {
        const uint triggerMessage = 0x8000 + 107;
        var poster = new ScriptedThreadMessagePoster(
            static (message, _) => message == EngineStaDispatcher.WorkMessage);
        var receiver = new FalsePositiveThreadMessageReceiver();
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(500),
            messagePoster: poster,
            messageReceiver: receiver);

        try
        {
            receiver.SimulateNextNoMessage();
            dispatcher.PostThreadMessage(triggerMessage);
            await receiver.FalsePositiveConsumed.Task.WaitAsync(TestContext.Current.CancellationToken);

            var eventOnlyWork = dispatcher.InvokeAsync(
                _ => ValueTask.FromResult((
                    dispatcher.HasThreadAccess,
                    Thread.CurrentThread.GetApartmentState())),
                CancellationToken.None);
            var result = await eventOnlyWork.WaitAsync(
                TimeSpan.FromMilliseconds(300),
                TestContext.Current.CancellationToken);

            Assert.True(result.HasThreadAccess);
            Assert.Equal(ApartmentState.STA, result.Item2);
            Assert.NotNull(dispatcher.LastPostingFailure);
            Assert.True(poster.AttemptCount(EngineStaDispatcher.WorkMessage) > 0);
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task Pump_QuitClearsNativeThreadIdentityBeforeLaterDispose()
    {
        const uint triggerMessage = 0x8000 + 108;
        var poster = new RecordingFailureThreadMessagePoster();
        var receiver = new QuitThreadMessageReceiver();
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(500),
            messagePoster: poster,
            messageReceiver: receiver);
        var retiredThreadId = dispatcher.NativeThreadId;

        try
        {
            Assert.NotEqual(0u, retiredThreadId);
            receiver.QuitNextReceive();
            PostNativeThreadMessage(dispatcher.NativeThreadId, triggerMessage);
            await dispatcher.ThreadCompletion.WaitAsync(TestContext.Current.CancellationToken);

            Assert.Equal(0u, dispatcher.NativeThreadId);
            await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
            Assert.DoesNotContain(retiredThreadId, poster.ThreadIds);
        }
        finally
        {
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task PostNativeMessage_AdmittedControlPostCannotOutliveNativeThreadIdentity()
    {
        const uint triggerMessage = 0x8000 + 109;
        using var poster = new PausingThreadMessagePoster(EngineStaDispatcher.ShutdownMessage);
        var receiver = new QuitThreadMessageReceiver();
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(500),
            messagePoster: poster,
            messageReceiver: receiver);
        poster.ObserveThreadCompletion(dispatcher.ThreadCompletion);
        var disposal = Task.Run(
            async () => await dispatcher.DisposeAsync(),
            TestContext.Current.CancellationToken);

        try
        {
            await poster.PostAdmitted.Task.WaitAsync(TestContext.Current.CancellationToken);
            receiver.QuitNextReceive();
            PostNativeThreadMessage(dispatcher.NativeThreadId, triggerMessage);
            await receiver.QuitConsumed.Task.WaitAsync(TestContext.Current.CancellationToken);

            var retirementWindow = Task.Delay(
                TimeSpan.FromMilliseconds(150),
                TestContext.Current.CancellationToken);
            var first = await Task.WhenAny(dispatcher.ThreadCompletion, retirementWindow);
            Assert.Same(retirementWindow, first);
            Assert.False(dispatcher.ThreadCompletion.IsCompleted);
        }
        finally
        {
            poster.ReleasePost();
            await disposal.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync();
        }

        Assert.NotEqual(0u, poster.AdmittedThreadId);
        Assert.False(poster.ThreadCompletionObservedBeforeNativePost);
    }

    [Fact]
    public async Task PostThreadMessage_AdmittedDirectPostCannotOutliveNativeThreadIdentity()
    {
        const uint directMessage = 0x8000 + 112;
        const uint quitTriggerMessage = 0x8000 + 113;
        using var poster = new PausingThreadMessagePoster(directMessage);
        var receiver = new QuitThreadMessageReceiver();
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(500),
            messagePoster: poster,
            messageReceiver: receiver);
        poster.ObserveThreadCompletion(dispatcher.ThreadCompletion);
        var directPost = Task.Run(
            () => dispatcher.PostThreadMessage(directMessage),
            TestContext.Current.CancellationToken);

        try
        {
            var admissionWindow = Task.Delay(
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);
            var admittedFirst = await Task.WhenAny(poster.PostAdmitted.Task, admissionWindow);
            Assert.Same(poster.PostAdmitted.Task, admittedFirst);

            receiver.QuitNextReceive();
            PostNativeThreadMessage(dispatcher.NativeThreadId, quitTriggerMessage);
            await receiver.QuitConsumed.Task.WaitAsync(TestContext.Current.CancellationToken);

            var retirementWindow = Task.Delay(
                TimeSpan.FromMilliseconds(150),
                TestContext.Current.CancellationToken);
            var first = await Task.WhenAny(dispatcher.ThreadCompletion, retirementWindow);
            Assert.Same(retirementWindow, first);
            Assert.False(dispatcher.ThreadCompletion.IsCompleted);
        }
        finally
        {
            poster.ReleasePost();
            await directPost.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            await dispatcher.DisposeAsync();
        }

        Assert.NotEqual(0u, poster.AdmittedThreadId);
        Assert.False(poster.ThreadCompletionObservedBeforeNativePost);
    }

    [Fact]
    public async Task UnsolicitedQuit_CancelsInFlightActionTokenBeforeTerminalCleanup()
    {
        const uint triggerMessage = 0x8000 + 111;
        var receiver = new QuitThreadMessageReceiver();
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(300),
            messageReceiver: receiver);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionObservedCancellation = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        CancellationToken suppliedToken = default;
        var operation = dispatcher.InvokeAsync(async cancellationToken =>
        {
            suppliedToken = cancellationToken;
            registration = cancellationToken.Register(
                () => callbackRan.TrySetResult(),
                useSynchronizationContext: false);
            started.TrySetResult();
            try
            {
                await release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                actionObservedCancellation.TrySetResult();
                throw;
            }
        }, CancellationToken.None);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            receiver.QuitNextReceive();
            dispatcher.PostThreadMessage(triggerMessage);
            await receiver.QuitConsumed.Task.WaitAsync(TestContext.Current.CancellationToken);
            await dispatcher.ThreadCompletion.WaitAsync(TestContext.Current.CancellationToken);

            Assert.True(suppliedToken.IsCancellationRequested);
            await callbackRan.Task.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);
            await actionObservedCancellation.Task.WaitAsync(
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        finally
        {
            release.TrySetResult();
            try
            {
                await operation;
            }
            catch
            {
                // Unsolicited quit terminally cancels the caller task.
            }

            await registration.DisposeAsync();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task DisposeAsync_RecordsOffStaTerminalWakeFailureAndStillTerminates()
    {
        var poster = new ScriptedThreadMessagePoster(
            static (message, attempt) =>
                message == EngineStaDispatcher.ShutdownMessage && attempt == 2);
        var dispatcher = await EngineStaDispatcher.StartAsync(
            TimeSpan.FromMilliseconds(300),
            messagePoster: poster);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var operation = dispatcher.InvokeAsync(async _ =>
        {
            started.SetResult();
            await release.Task.ConfigureAwait(false);
        }, TestContext.Current.CancellationToken);

        try
        {
            await started.Task.WaitAsync(TestContext.Current.CancellationToken);
            var disposal = dispatcher.DisposeAsync().AsTask();
            release.SetResult();
            await disposal.WaitAsync(TestContext.Current.CancellationToken);

            Assert.NotNull(dispatcher.LastPostingFailure);
            Assert.True(poster.AttemptCount(EngineStaDispatcher.ShutdownMessage) >= 2);
            Assert.True(operation.IsCompleted);
            Assert.False(dispatcher.IsThreadAlive);
        }
        finally
        {
            release.TrySetResult();
            await dispatcher.DisposeAsync();
        }
    }

    [Fact]
    public async Task RepeatedStartDispose_DoesNotLeakNamedEngineThreads()
    {
        var dispatchers = new List<EngineStaDispatcher>();

        for (var index = 0; index < 20; index++)
        {
            var dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromSeconds(1));
            dispatchers.Add(dispatcher);
            try
            {
                await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
            }
            finally
            {
                await dispatcher.DisposeAsync();
            }
        }

        Assert.All(dispatchers, dispatcher => Assert.False(dispatcher.IsThreadAlive));
        Assert.All(dispatchers, dispatcher => Assert.StartsWith("VibeWallpaper.Engine.STA.", dispatcher.ThreadName));
        Assert.Equal(dispatchers.Count, dispatchers.Select(dispatcher => dispatcher.ThreadName).Distinct().Count());
    }

    private static ValueTask ThrowSynchronously(CancellationToken cancellationToken) =>
        throw new InvalidOperationException("injected");

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<DispatcherRetentionFixture> CreateForcedPendingDispatcherReferenceAsync()
    {
        var state = new PendingOperationState();
        if (Interlocked.CompareExchange(ref s_pendingRetentionState, state, null) is not null)
        {
            throw new InvalidOperationException("The retention fixture already has a pending action.");
        }

        EngineStaDispatcher? dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        var weakDispatcher = new WeakReference<EngineStaDispatcher>(dispatcher);
        Task? operation = dispatcher.InvokeAsync(
            WaitForPendingRetentionReleaseAsync,
            TestContext.Current.CancellationToken);

        await state.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        await dispatcher.DisposeAsync().AsTask().WaitAsync(TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        dispatcher = null;
        operation = null;
        return new DispatcherRetentionFixture(weakDispatcher, state);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<BlockedCallbackRetentionFixture> CreateBlockedCallbackRetentionFixtureAsync()
    {
        var state = new BlockedCallbackRetentionState();
        if (Interlocked.CompareExchange(ref s_blockedCallbackRetentionState, state, null) is not null)
        {
            throw new InvalidOperationException("The blocked-callback retention fixture is already active.");
        }

        EngineStaDispatcher? dispatcher = await EngineStaDispatcher.StartAsync(TimeSpan.FromMilliseconds(100));
        var weakDispatcher = new WeakReference<EngineStaDispatcher>(dispatcher);
        Task? operation = dispatcher.InvokeAsync(
            WaitForBlockedCancellationCallbackAsync,
            CancellationToken.None);

        await state.Started.Task.WaitAsync(TestContext.Current.CancellationToken);
        var disposal = dispatcher.DisposeAsync().AsTask();
        await state.CallbackEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        await disposal.WaitAsync(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        dispatcher = null;
        operation = null;
        return new BlockedCallbackRetentionFixture(weakDispatcher, state);
    }

    private static async ValueTask WaitForPendingRetentionReleaseAsync(CancellationToken _)
    {
        var state = Interlocked.Exchange(ref s_pendingRetentionState, null) ??
            throw new InvalidOperationException("The retention fixture did not publish its action state.");
        state.Started.TrySetResult();
        await state.Release.Task.ConfigureAwait(false);
    }

    private static async ValueTask WaitForBlockedCancellationCallbackAsync(CancellationToken cancellationToken)
    {
        var state = Interlocked.Exchange(ref s_blockedCallbackRetentionState, null) ??
            throw new InvalidOperationException("The blocked-callback retention fixture did not publish its state.");
        using var registration = cancellationToken.Register(
            static callbackState =>
            {
                var fixtureState = (BlockedCallbackRetentionState)callbackState!;
                fixtureState.CallbackEntered.TrySetResult();
                try
                {
                    if (!fixtureState.ReleaseCallback.Wait(TimeSpan.FromSeconds(5)))
                    {
                        throw new TimeoutException("Test did not release the blocked cancellation callback.");
                    }
                }
                finally
                {
                    fixtureState.CallbackExited.TrySetResult();
                }
            },
            state,
            useSynchronizationContext: false);
        state.Started.TrySetResult();
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsTargetAlive(WeakReference<EngineStaDispatcher> dispatcher) =>
        dispatcher.TryGetTarget(out _);

    private static void PostNativeThreadMessage(uint threadId, uint message)
    {
        if (!User32.PostThreadMessage(threadId, message, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private sealed record DispatcherRetentionFixture(
        WeakReference<EngineStaDispatcher> Dispatcher,
        PendingOperationState State);

    private sealed record BlockedCallbackRetentionFixture(
        WeakReference<EngineStaDispatcher> Dispatcher,
        BlockedCallbackRetentionState State);

    private sealed class PendingOperationState
    {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

    }

    private sealed class BlockedCallbackRetentionState
    {
        internal TaskCompletionSource Started { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallbackEntered { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CallbackExited { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        internal ManualResetEventSlim ReleaseCallback { get; } = new();
    }
}

internal sealed class ScriptedThreadMessagePoster(Func<uint, int, bool> shouldFail) : IThreadMessagePoster
{
    private readonly ConcurrentDictionary<uint, int> _attempts = new();

    public int AttemptCount(uint message) => _attempts.GetValueOrDefault(message);

    public Exception? TryPost(uint threadId, uint message)
    {
        var attempt = _attempts.AddOrUpdate(message, 1, static (_, current) => current + 1);
        if (shouldFail(message, attempt))
        {
            return new Win32Exception(1234, $"Injected post failure for message {message} attempt {attempt}.");
        }

        return User32.PostThreadMessage(threadId, message, 0, 0)
            ? null
            : new Win32Exception(Marshal.GetLastPInvokeError());
    }
}

internal sealed class SwitchableThreadMessageReceiver : IThreadMessageReceiver
{
    private int _failNextReceive;

    internal void FailNextReceive() => Interlocked.Exchange(ref _failNextReceive, 1);

    public ThreadMessageReceiveResult TryReceive(out User32.Message message)
    {
        if (Interlocked.Exchange(ref _failNextReceive, 0) != 0)
        {
            message = default;
            return ThreadMessageReceiveResult.Error;
        }

        return NativeThreadMessageReceiver.Instance.TryReceive(out message);
    }
}

internal sealed class FalsePositiveThreadMessageReceiver : IThreadMessageReceiver
{
    private int _simulateNextNoMessage;

    internal TaskCompletionSource FalsePositiveConsumed { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal void SimulateNextNoMessage() => Interlocked.Exchange(ref _simulateNextNoMessage, 1);

    public ThreadMessageReceiveResult TryReceive(out User32.Message message)
    {
        if (Interlocked.Exchange(ref _simulateNextNoMessage, 0) != 0)
        {
            if (!User32.PeekMessage(out message, 0, 0, 0, User32.PmRemove))
            {
                throw new InvalidOperationException("The injected message-input wake had no queued message to consume.");
            }

            FalsePositiveConsumed.TrySetResult();
            message = default;
            return ThreadMessageReceiveResult.NoMessage;
        }

        return NativeThreadMessageReceiver.Instance.TryReceive(out message);
    }
}

internal sealed class QuitThreadMessageReceiver : IThreadMessageReceiver
{
    private int _quitNextReceive;

    internal TaskCompletionSource QuitConsumed { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    internal void QuitNextReceive() => Interlocked.Exchange(ref _quitNextReceive, 1);

    public ThreadMessageReceiveResult TryReceive(out User32.Message message)
    {
        if (Interlocked.Exchange(ref _quitNextReceive, 0) != 0)
        {
            message = default;
            QuitConsumed.TrySetResult();
            return ThreadMessageReceiveResult.Quit;
        }

        return NativeThreadMessageReceiver.Instance.TryReceive(out message);
    }
}

internal sealed class PausingThreadMessagePoster(uint messageToPause) : IThreadMessagePoster, IDisposable
{
    private readonly ManualResetEventSlim _releasePost = new();
    private Task? _threadCompletion;
    private int _pauseClaimed;

    internal TaskCompletionSource PostAdmitted { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    internal uint AdmittedThreadId { get; private set; }
    internal bool ThreadCompletionObservedBeforeNativePost { get; private set; }

    internal void ObserveThreadCompletion(Task threadCompletion) =>
        _threadCompletion = threadCompletion;

    internal void ReleasePost() => _releasePost.Set();

    public Exception? TryPost(uint threadId, uint message)
    {
        if (message == messageToPause && Interlocked.Exchange(ref _pauseClaimed, 1) == 0)
        {
            AdmittedThreadId = threadId;
            PostAdmitted.TrySetResult();
            if (!_releasePost.Wait(TimeSpan.FromSeconds(5)))
            {
                return new TimeoutException("Test did not release the paused native post.");
            }

            ThreadCompletionObservedBeforeNativePost =
                _threadCompletion?.IsCompleted ??
                throw new InvalidOperationException("Thread completion was not attached to the poster.");
        }

        return NativeThreadMessagePoster.Instance.TryPost(threadId, message);
    }

    public void Dispose() => _releasePost.Dispose();
}

internal sealed class RecordingFailureThreadMessagePoster : IThreadMessagePoster
{
    private readonly ConcurrentQueue<uint> _threadIds = new();

    internal IReadOnlyCollection<uint> ThreadIds => _threadIds.ToArray();

    public Exception? TryPost(uint threadId, uint message)
    {
        _threadIds.Enqueue(threadId);
        return new Win32Exception(1234, $"Injected post failure for retired thread {threadId}.");
    }
}
