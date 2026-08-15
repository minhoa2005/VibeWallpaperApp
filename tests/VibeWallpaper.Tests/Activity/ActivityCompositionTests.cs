using VibeWallpaper.App.Coordination;
using VibeWallpaper.Engine.Activity;
using VibeWallpaper.Engine.Core.Activity;

namespace VibeWallpaper.Tests.Activity;

public sealed class ActivityCompositionTests
{
    [Fact]
    public void WindowsSystemFactsProvider_CapturesFreshNativePowerAndRemoteFactsWithObserverState()
    {
        var native = new MutableSystemFactsNative
        {
            RunningOnBattery = true,
            BatterySaverEnabled = false,
            RemoteDesktopSession = true,
        };
        var provider = new WindowsActivitySystemFactsProvider(native);
        provider.SetSessionLocked(true);
        provider.SetDisplayOff(true);
        provider.SetSystemSleeping(true);

        var first = provider.Capture();
        native.RunningOnBattery = false;
        native.BatterySaverEnabled = true;
        native.RemoteDesktopSession = false;
        var second = provider.Capture();

        Assert.Equal(new ActivitySystemFacts(true, true, true, true, false, true), first);
        Assert.Equal(new ActivitySystemFacts(true, true, true, false, true, false), second);
        Assert.Equal(2, native.CaptureCount);
    }

    [Fact]
    public void WindowsWindowContextProvider_UsesCurrentDesktopParentAndCopiesOwnedWindows()
    {
        var owned = new List<nint> { 101, 202 };
        var provider = new WindowsActivityWindowContextProvider(
            () => owned,
            new FixedWindowContextNative(999));

        var context = provider.Capture();
        owned.Clear();

        Assert.Equal(999, context.DesktopHostHwnd);
        Assert.Equal([101, 202], context.ApplicationOwnedWindows.Order());
    }

    [Fact]
    public async Task ActivityObservationServices_StartsRegistrationsBeforeMonitorAndStopsMonitorBeforeUnregistering()
    {
        var events = new List<string>();
        var monitor = new RecordingMonitor(events);
        var first = new RecordingObserver("first", events);
        var second = new RecordingObserver("second", events);
        await using var services = new ActivityObservationServices(
            monitor,
            [first, second],
            static (_, _) => Task.CompletedTask);

        services.Start();
        await services.DisposeAsync();

        Assert.Equal(
            ["start:first", "start:second", "start:monitor", "stop:monitor", "stop:second", "stop:first", "dispose:monitor"],
            events);
    }

    [Fact]
    public void WindowEventObserver_RegistersAndUnregistersExactlyOnce()
    {
        var registrations = 0;
        var disposals = 0;
        using var observer = new WindowEventObserver(
            new RecordingSink(),
            _ =>
            {
                registrations++;
                return new CallbackDisposable(() => disposals++);
            });

        observer.Start();
        observer.Start();
        observer.Dispose();
        observer.Dispose();

        Assert.Equal(1, registrations);
        Assert.Equal(1, disposals);
    }

    [Fact]
    public void PowerSessionObserver_StartsAndDisposesNativeRegistrationExactlyOnce()
    {
        var registrations = 0;
        var disposals = 0;
        using var observer = new PowerSessionObserver(
            new RecordingSink(),
            _ =>
            {
                registrations++;
                return new CallbackDisposable(() => disposals++);
            });

        observer.Start();
        observer.Start();
        observer.Dispose();
        observer.Dispose();

        Assert.Equal(1, registrations);
        Assert.Equal(1, disposals);
    }

    [Fact]
    public void PowerSessionObserver_CallbackOnlyEnqueuesImmutableEvidence()
    {
        var native = new MutableSystemFactsNative();
        var facts = new WindowsActivitySystemFactsProvider(native);
        var sink = new RecordingSink();
        using var observer = new PowerSessionObserver(sink, facts, []);

        observer.SessionLocked();

        Assert.False(facts.Capture().SessionLocked);
        Assert.Equal([new ActivityEvidence(ActivityEvidenceKind.SessionLocked)], sink.Evidence);
    }

    [Fact]
    public void PowerSessionObserver_LateNativeCallbackAfterDisposeIsIgnored()
    {
        var sink = new RecordingSink();
        var observer = new PowerSessionObserver(sink, []);
        observer.Dispose();

        var exception = Record.Exception(observer.SessionLocked);

        Assert.Null(exception);
        Assert.Empty(sink.Evidence);
    }

    [Fact]
    public void ActivityObserversStage_UsesTheApplicationCoordinatorLifecycleKind()
    {
        var stage = new ActivityObserversStage(
            () => new ActivityObservationServices(
                new RecordingMonitor([]),
                [],
                static (_, _) => Task.CompletedTask));

        Assert.Equal(ApplicationStageKind.ActivityObservers, stage.Kind);
    }

    private sealed class MutableSystemFactsNative : IActivitySystemFactsNativeApi
    {
        public bool RunningOnBattery { get; set; }
        public bool BatterySaverEnabled { get; set; }
        public bool RemoteDesktopSession { get; set; }
        public int CaptureCount { get; private set; }

        public ActivityNativeSystemFacts Capture()
        {
            CaptureCount++;
            return new ActivityNativeSystemFacts(RunningOnBattery, BatterySaverEnabled, RemoteDesktopSession);
        }
    }

    private sealed class FixedWindowContextNative(nint parent) : IActivityWindowContextNativeApi
    {
        public nint GetParent(nint hwnd) => parent;
    }

    private sealed class RecordingMonitor(List<string> events) : IActivityMonitor
    {
        public event ActivitySnapshotPublishedHandler? SnapshotPublished;
        public ActivitySnapshot? Current => null;
        public void Start() => events.Add("start:monitor");
        public void Enqueue(ActivityEvidence evidence) { }
        public void Stop() => events.Add("stop:monitor");
        public void Dispose() => events.Add("dispose:monitor");
        public void Publish(ActivitySnapshot snapshot) => SnapshotPublished?.Invoke(this, snapshot);
    }

    private sealed class RecordingObserver(string name, List<string> events) : IActivityObserver
    {
        public void Start() => events.Add($"start:{name}");
        public void Dispose() => events.Add($"stop:{name}");
    }

    private sealed class RecordingSink : IActivityEvidenceSink
    {
        public List<ActivityEvidence> Evidence { get; } = [];
        public void Enqueue(ActivityEvidence evidence) => Evidence.Add(evidence);
    }

    private sealed class CallbackDisposable(Action callback) : IDisposable
    {
        public void Dispose() => callback();
    }
}
