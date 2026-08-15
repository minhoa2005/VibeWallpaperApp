using System.ComponentModel;
using System.Runtime.InteropServices;

namespace VibeWallpaper.Engine.Activity;

public sealed class WindowEventObserver : IActivityObserver
{
    private readonly object _gate = new();
    private readonly IActivityEvidenceSink _sink;
    private readonly Func<Action<ActivityEvidence>, IDisposable> _register;
    private IDisposable? _registration;
    private bool _disposed;

    public WindowEventObserver(IActivityEvidenceSink sink)
        : this(sink, NativeWindowEventRegistration.Register)
    {
    }

    internal WindowEventObserver(
        IActivityEvidenceSink sink,
        Func<Action<ActivityEvidence>, IDisposable> register)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(register);
        _sink = sink;
        _register = register;
    }

    public void Start()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _registration ??= _register(Enqueue);
        }
    }

    public void TopologyReconciled() => Enqueue(new ActivityEvidence(ActivityEvidenceKind.TopologyReconciled));

    public void HostInvalidated() => Enqueue(new ActivityEvidence(ActivityEvidenceKind.HostInvalidated));

    public void MonitorRemoved(VibeWallpaper.Engine.Core.Monitors.MonitorIdentity output) =>
        Enqueue(new ActivityEvidence(ActivityEvidenceKind.MonitorRemoved, output));

    public void Dispose()
    {
        IDisposable? registration;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            registration = _registration;
            _registration = null;
        }

        registration?.Dispose();
    }

    private void Enqueue(ActivityEvidence evidence)
    {
        lock (_gate)
        {
            if (!_disposed) _sink.Enqueue(evidence with { });
        }
    }
}

internal sealed class NativeWindowEventRegistration : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectReorder = 0x8004;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint WineventOutOfContext = 0;
    private const uint WineventSkipOwnProcess = 2;

    private readonly NativeMethods.WinEventProc _callback;
    private readonly Action<ActivityEvidence> _publish;
    private readonly List<nint> _hooks = [];
    private int _disposed;

    private NativeWindowEventRegistration(Action<ActivityEvidence> publish)
    {
        _publish = publish;
        _callback = OnWinEvent;
    }

    public static IDisposable Register(Action<ActivityEvidence> publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        var registration = new NativeWindowEventRegistration(publish);
        try
        {
            registration.Add(EventSystemForeground);
            registration.Add(EventObjectShow);
            registration.Add(EventObjectHide);
            registration.Add(EventObjectReorder);
            registration.Add(EventObjectLocationChange);
            registration.Add(EventObjectCloaked);
            registration.Add(EventObjectUncloaked);
            return registration;
        }
        catch
        {
            registration.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var hook in _hooks) _ = NativeMethods.UnhookWinEvent(hook);
        _hooks.Clear();
        GC.KeepAlive(_callback);
    }

    private void Add(uint eventId)
    {
        var hook = NativeMethods.SetWinEventHook(
            eventId,
            eventId,
            0,
            _callback,
            0,
            0,
            WineventOutOfContext | WineventSkipOwnProcess);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        _hooks.Add(hook);
    }

    private void OnWinEvent(
        nint hook,
        uint eventId,
        nint hwnd,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var kind = eventId switch
        {
            EventSystemForeground => ActivityEvidenceKind.ForegroundChanged,
            EventObjectLocationChange => ActivityEvidenceKind.LocationChanged,
            EventObjectReorder => ActivityEvidenceKind.ZOrderChanged,
            _ => ActivityEvidenceKind.FullscreenChanged,
        };
        _publish(new ActivityEvidence(kind));
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate void WinEventProc(
            nint hook,
            uint eventId,
            nint hwnd,
            int objectId,
            int childId,
            uint eventThread,
            uint eventTime);

        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWinEventHook(
            uint eventMin,
            uint eventMax,
            nint eventHookModule,
            WinEventProc callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(nint hook);
    }
}
