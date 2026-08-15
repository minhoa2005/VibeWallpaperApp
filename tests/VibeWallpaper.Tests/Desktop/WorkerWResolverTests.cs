using VibeWallpaper.Engine.Desktop;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.Desktop;

public sealed class WorkerWResolverTests
{
    [Fact]
    public async Task Resolve_CanonicalExplorerTree_SelectsWorkerWSibling()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeDesktopShellWindowApi(progman: 1)
            .AddTopLevel(10, "WorkerW")
            .AddChild(10, 11, "SHELLDLL_DefView")
            .AddTopLevel(20, "WorkerW");
        var resolver = new WorkerWResolver(dispatcher, windows, maximumAttempts: 2);

        var result = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(20, result.ParentHwnd);
        Assert.Equal("WorkerWSibling", result.Strategy);
        Assert.False(result.IsDegraded);
        Assert.Null(result.Diagnostic);
    }

    [Fact]
    public async Task Resolve_DefViewDirectlyUnderProgman_UsesProgman()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeDesktopShellWindowApi(progman: 1)
            .AddTopLevel(1, "Progman")
            .AddChild(1, 2, "SHELLDLL_DefView");
        var resolver = new WorkerWResolver(dispatcher, windows, maximumAttempts: 2);

        var result = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ParentHwnd);
        Assert.Equal("ProgmanDefView", result.Strategy);
        Assert.False(result.IsDegraded);
    }

    [Fact]
    public async Task Resolve_RaisedDesktop_SelectsProgmanLayerBetweenShellViewAndWallpaperWorker()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeDesktopShellWindowApi(progman: 1)
            .AddTopLevel(1, "Progman")
            .AddChild(1, 2, "SHELLDLL_DefView")
            .AddChild(1, 3, "WorkerW")
            .SetExtendedStyle(1, FakeDesktopShellWindowApi.NoRedirectionBitmapExtendedStyle);
        var resolver = new WorkerWResolver(dispatcher, windows, maximumAttempts: 2);

        var result = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ParentHwnd);
        Assert.Equal("ProgmanRaisedDesktop", result.Strategy);
        Assert.Equal(2, result.ShellViewHwnd);
        Assert.True(result.RequiresLayeredChildren);
        Assert.False(result.IsDegraded);
        Assert.Equal((nuint)0xD, windows.LastMessageWParam);
        Assert.Equal((nint)1, windows.LastMessageLParam);
    }

    [Fact]
    public async Task Resolve_WhenSiblingHandleIsStale_ReturnsTypedDegradedResult()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeDesktopShellWindowApi(progman: 1)
            .AddTopLevel(10, "WorkerW")
            .AddChild(10, 11, "SHELLDLL_DefView")
            .AddTopLevel(20, "WorkerW")
            .MakeStale(20);
        var resolver = new WorkerWResolver(dispatcher, windows, maximumAttempts: 2);

        var result = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ParentHwnd);
        Assert.Equal("Unavailable", result.Strategy);
        Assert.True(result.IsDegraded);
        Assert.Contains("stale", result.Diagnostic, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Resolve_WhenExplorerIsAbsent_ReturnsTypedDegradedResultWithoutUnboundedRetry()
    {
        await using var dispatcher = await EngineStaDispatcher.StartAsync();
        var windows = new FakeDesktopShellWindowApi(progman: 0);
        var resolver = new WorkerWResolver(dispatcher, windows, maximumAttempts: 3);

        var result = await resolver.ResolveAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new DesktopHostResolution(0, "Unavailable", true, "Progman was not found after 3 attempts."), result);
        Assert.Equal(3, windows.FindProgmanCount);
    }
}

internal sealed class FakeDesktopShellWindowApi(nint progman) : IDesktopShellWindowApi
{
    internal const nint NoRedirectionBitmapExtendedStyle = 0x00200000;
    private readonly List<nint> _topLevel = [];
    private readonly Dictionary<nint, string> _classes = [];
    private readonly Dictionary<(nint Parent, string Class), nint> _children = [];
    private readonly Dictionary<nint, nint> _extendedStyles = [];
    private readonly HashSet<nint> _stale = [];

    public int FindProgmanCount { get; private set; }
    public nuint LastMessageWParam { get; private set; }
    public nint LastMessageLParam { get; private set; }

    public FakeDesktopShellWindowApi AddTopLevel(nint hwnd, string className)
    {
        _topLevel.Add(hwnd);
        _classes[hwnd] = className;
        return this;
    }

    public FakeDesktopShellWindowApi AddChild(nint parent, nint hwnd, string className)
    {
        _classes[hwnd] = className;
        _children[(parent, className)] = hwnd;
        return this;
    }

    public FakeDesktopShellWindowApi MakeStale(nint hwnd)
    {
        _stale.Add(hwnd);
        return this;
    }

    public FakeDesktopShellWindowApi SetExtendedStyle(nint hwnd, nint style)
    {
        _extendedStyles[hwnd] = style;
        return this;
    }

    public nint FindTopLevelWindow(string className)
    {
        FindProgmanCount++;
        return className == "Progman" ? progman : 0;
    }

    public bool TrySendMessageTimeout(
        nint hwnd,
        uint message,
        nuint wParam,
        nint lParam,
        uint timeoutMilliseconds,
        out int errorCode)
    {
        LastMessageWParam = wParam;
        LastMessageLParam = lParam;
        errorCode = 0;
        return true;
    }

    public nint GetExtendedWindowStyle(nint hwnd) => _extendedStyles.GetValueOrDefault(hwnd);

    public IReadOnlyList<nint> EnumerateTopLevelWindows() => _topLevel;

    public nint FindChildWindow(nint parent, string className) =>
        _children.GetValueOrDefault((parent, className));

    public nint FindNextSiblingWindow(nint hwnd, string className)
    {
        var index = _topLevel.IndexOf(hwnd);
        for (var candidateIndex = index + 1; candidateIndex < _topLevel.Count; candidateIndex++)
        {
            var candidate = _topLevel[candidateIndex];
            if (_classes.GetValueOrDefault(candidate) == className)
            {
                return candidate;
            }
        }

        return 0;
    }

    public bool IsWindow(nint hwnd) => hwnd != 0 && !_stale.Contains(hwnd);
}
