using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Engine.Desktop;

internal interface IDesktopShellWindowApi
{
    nint FindTopLevelWindow(string className);
    bool TrySendMessageTimeout(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam,
        uint timeoutMilliseconds,
        out int errorCode);
    nint GetExtendedWindowStyle(nint hwnd);
    IReadOnlyList<nint> EnumerateTopLevelWindows();
    nint FindChildWindow(nint parent, string className);
    nint FindNextSiblingWindow(nint hwnd, string className);
    bool IsWindow(nint hwnd);
}

internal sealed class WorkerWResolver : IDesktopHostResolver
{
    internal const uint SpawnWorkerWMessage = 0x052C;
    internal const nint NoRedirectionBitmapExtendedStyle = 0x00200000;
    private const nuint RaisedDesktopWParam = 0xD;
    private const nint RaisedDesktopLParam = 1;
    private const uint MaximumMessageTimeoutMilliseconds = 1000;
    private readonly IEngineDispatcher _dispatcher;
    private readonly IDesktopShellWindowApi _windows;
    private readonly int _maximumAttempts;

    internal WorkerWResolver(
        IEngineDispatcher dispatcher,
        IDesktopShellWindowApi windows,
        int maximumAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(windows);
        if (maximumAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAttempts));
        }

        _dispatcher = dispatcher;
        _windows = windows;
        _maximumAttempts = maximumAttempts;
    }

    internal Task<DesktopHostResolution> ResolveAsync(CancellationToken cancellationToken = default) =>
        _dispatcher.InvokeAsync(
            token => ValueTask.FromResult(ResolveCore(token)),
            cancellationToken);

    public DesktopHostResolution Resolve()
    {
        AssertEngineThread();
        return ResolveCore(CancellationToken.None);
    }

    private DesktopHostResolution ResolveCore(CancellationToken cancellationToken)
    {
        AssertEngineThread();
        string? lastDiagnostic = null;
        for (var attempt = 1; attempt <= _maximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var progman = _windows.FindTopLevelWindow("Progman");
            if (!IsUsable(progman))
            {
                lastDiagnostic = $"Progman was not found after {_maximumAttempts} attempts.";
                continue;
            }

            var timeout = Math.Min(MaximumMessageTimeoutMilliseconds, checked((uint)attempt * 250));
            if (!_windows.TrySendMessageTimeout(
                    progman,
                    SpawnWorkerWMessage,
                    RaisedDesktopWParam,
                    RaisedDesktopLParam,
                    timeout,
                    out var errorCode))
            {
                lastDiagnostic = $"WorkerW shell message failed with Win32 error {errorCode} on attempt {attempt}.";
                continue;
            }

            foreach (var topLevel in _windows.EnumerateTopLevelWindows())
            {
                if (!IsUsable(topLevel))
                {
                    lastDiagnostic = $"Explorer returned stale HWND 0x{topLevel:X}.";
                    continue;
                }

                var defView = _windows.FindChildWindow(topLevel, "SHELLDLL_DefView");
                if (defView == 0)
                {
                    continue;
                }

                if (!IsUsable(defView))
                {
                    lastDiagnostic = $"SHELLDLL_DefView HWND 0x{defView:X} became stale.";
                    continue;
                }

                if (topLevel == progman)
                {
                    if (IsRaisedDesktop(progman))
                    {
                        var wallpaperWorker = _windows.FindChildWindow(progman, "WorkerW");
                        if (!IsUsable(wallpaperWorker))
                        {
                            lastDiagnostic = wallpaperWorker == 0
                                ? "Raised desktop Progman had no wallpaper WorkerW child."
                                : $"Raised desktop WorkerW HWND 0x{wallpaperWorker:X} was stale.";
                            continue;
                        }

                        return new DesktopHostResolution(
                            progman,
                            "ProgmanRaisedDesktop",
                            false,
                            null,
                            ShellViewHwnd: defView,
                            RequiresLayeredChildren: true);
                    }

                    return new DesktopHostResolution(progman, "ProgmanDefView", false, null);
                }

                var workerW = _windows.FindNextSiblingWindow(topLevel, "WorkerW");
                if (!IsUsable(workerW))
                {
                    lastDiagnostic = workerW == 0
                        ? "The SHELLDLL_DefView container had no WorkerW sibling."
                        : $"The WorkerW sibling HWND 0x{workerW:X} was stale.";
                    continue;
                }

                return new DesktopHostResolution(workerW, "WorkerWSibling", false, null);
            }

            lastDiagnostic ??= $"SHELLDLL_DefView was not found on attempt {attempt}.";
        }

        return new DesktopHostResolution(0, "Unavailable", true, lastDiagnostic);
    }

    private bool IsUsable(nint hwnd) => hwnd != 0 && _windows.IsWindow(hwnd);

    private bool IsRaisedDesktop(nint progman) =>
        (_windows.GetExtendedWindowStyle(progman) & NoRedirectionBitmapExtendedStyle) != 0;

    private void AssertEngineThread()
    {
        if (!_dispatcher.HasThreadAccess)
        {
            throw new InvalidOperationException("Desktop HWND discovery must run on the engine thread.");
        }
    }
}
