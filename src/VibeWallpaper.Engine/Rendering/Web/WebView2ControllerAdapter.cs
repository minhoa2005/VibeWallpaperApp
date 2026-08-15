using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using Windows.Foundation;

namespace VibeWallpaper.Engine.Rendering.Web;

public sealed class WebView2ControllerAdapter : IWebControllerAdapter
{
    private readonly WallpaperDefinition _definition;
    private readonly string _userDataFolder;
    private readonly WebSecurityPolicy _security = new();
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _webView;
    private WebContentPolicy? _contentPolicy;
    private string? _mappedHost;
    private bool _disposed;

    public WebView2ControllerAdapter(WallpaperDefinition definition, string userDataFolder)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (definition.Source is not WebSource)
            throw new ArgumentException("The WebView2 adapter requires a web wallpaper.", nameof(definition));
        if (string.IsNullOrWhiteSpace(userDataFolder) || !Path.IsPathFullyQualified(userDataFolder))
            throw new ArgumentException("An absolute WebView2 user-data folder is required.", nameof(userDataFolder));
        _definition = definition;
        _userDataFolder = Path.GetFullPath(userDataFolder);
    }

    public async Task InitializeAsync(RendererContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ThrowIfDisposed();
        if (_controller is not null) throw new InvalidOperationException("The WebView2 controller is already initialized.");
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_userDataFolder);
        _environment = await CoreWebView2Environment.CreateWithOptionsAsync(
            string.Empty,
            _userDataFolder,
            new CoreWebView2EnvironmentOptions());
        cancellationToken.ThrowIfCancellationRequested();
        var parentWindow = CoreWebView2ControllerWindowReference.CreateFromWindowHandle(
            unchecked((ulong)context.HostHwnd.ToInt64()));
        _controller = await _environment.CreateCoreWebView2ControllerAsync(parentWindow);
        cancellationToken.ThrowIfCancellationRequested();
        _controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
        _controller.Bounds = new Rect(0, 0, context.Viewport.Width, context.Viewport.Height);
        _controller.IsVisible = false;
        _webView = _controller.CoreWebView2;
        ConfigureSettings(_webView.Settings);
        _webView.NavigationStarting += OnNavigationStarting;
        _webView.NewWindowRequested += OnNewWindowRequested;
        _webView.PermissionRequested += OnPermissionRequested;
        _webView.DownloadStarting += OnDownloadStarting;
        _webView.WebResourceRequested += OnWebResourceRequested;
        _webView.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
    }

    public async Task NavigateAsync(WebSource source, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ThrowIfDisposed();
        var webView = _webView ?? throw new InvalidOperationException("The WebView2 controller is not initialized.");
        var entryPath = Path.GetFullPath(Path.Combine(source.DirectoryPath, source.EntryPoint));
        if (!File.Exists(entryPath)) throw new FileNotFoundException("The web wallpaper entry point is missing.", entryPath);
        var origin = WebWallpaperOrigin.Create(_definition.Id);
        _contentPolicy = new WebContentPolicy(
            _definition.Id,
            source.DirectoryPath,
            origin,
            _definition.NetworkEnabled);
        if (!_contentPolicy.IsPathAllowed(entryPath))
            throw new InvalidOperationException("The web wallpaper entry point escapes its canonical root.");

        if (_mappedHost is not null) webView.ClearVirtualHostNameToFolderMapping(_mappedHost);
        _mappedHost = origin.Host;
        webView.SetVirtualHostNameToFolderMapping(
            _mappedHost,
            source.DirectoryPath,
            CoreWebView2HostResourceAccessKind.DenyCors);
        var relativeUri = source.EntryPoint.Replace('\\', '/');
        var navigationUri = new Uri(origin, relativeUri).AbsoluteUri;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            if (args.IsSuccess) completion.TrySetResult();
            else completion.TrySetException(new InvalidOperationException($"WebView2 navigation failed: {args.WebErrorStatus}."));
        }

        webView.NavigationCompleted += OnCompleted;
        try
        {
            webView.Navigate(navigationUri);
            await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            webView.NavigationCompleted -= OnCompleted;
        }
    }

    public Task SetVisibleAsync(bool visible, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        (_controller ?? throw new InvalidOperationException("The WebView2 controller is not initialized.")).IsVisible = visible;
        return Task.CompletedTask;
    }

    public Task SetPresentationThrottleAsync(int? targetPresentationFps, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        var webView = _webView ?? throw new InvalidOperationException("The WebView2 controller is not initialized.");
        var message = JsonSerializer.Serialize(new
        {
            type = "vibeWallpaper.performance",
            targetPresentationFps,
        });
        webView.PostWebMessageAsJson(message);
        return Task.CompletedTask;
    }

    public async Task<bool> TrySuspendAsync(CancellationToken cancellationToken)
    {
        ThrowIfDisposed();
        var webView = _webView ?? throw new InvalidOperationException("The WebView2 controller is not initialized.");
        cancellationToken.ThrowIfCancellationRequested();
        var suspended = await webView.TrySuspendAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return suspended;
    }

    public Task ResumeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        (_webView ?? throw new InvalidOperationException("The WebView2 controller is not initialized.")).Resume();
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        if (_webView is not null)
        {
            _webView.NavigationStarting -= OnNavigationStarting;
            _webView.NewWindowRequested -= OnNewWindowRequested;
            _webView.PermissionRequested -= OnPermissionRequested;
            _webView.DownloadStarting -= OnDownloadStarting;
            _webView.WebResourceRequested -= OnWebResourceRequested;
            if (_mappedHost is not null) _webView.ClearVirtualHostNameToFolderMapping(_mappedHost);
        }
        _controller?.Close();
        _webView = null;
        _controller = null;
        _environment = null;
        return ValueTask.CompletedTask;
    }

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs args)
    {
        if (_contentPolicy is null || !Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
            _security.Decide(_contentPolicy, uri, WebResourceKind.Navigation) == WebRequestDecision.Block)
        {
            args.Cancel = true;
        }
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs args) =>
        args.Handled = true;

    private static void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs args)
    {
        args.State = CoreWebView2PermissionState.Deny;
        args.SavesInProfile = false;
        args.Handled = true;
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs args)
    {
        args.Cancel = true;
        args.Handled = true;
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (_contentPolicy is null || _environment is null ||
            !Uri.TryCreate(args.Request.Uri, UriKind.Absolute, out var uri) ||
            _security.Decide(_contentPolicy, uri, MapResourceKind(args.ResourceContext)) == WebRequestDecision.Block)
        {
            args.Response = _environment?.CreateWebResourceResponse(
                null, 403, "Blocked", "Content-Type: text/plain; charset=utf-8");
        }
    }

    private static WebResourceKind MapResourceKind(CoreWebView2WebResourceContext context) => context switch
    {
        CoreWebView2WebResourceContext.Document => WebResourceKind.Navigation,
        CoreWebView2WebResourceContext.XmlHttpRequest => WebResourceKind.Fetch,
        CoreWebView2WebResourceContext.Fetch => WebResourceKind.Fetch,
        _ => WebResourceKind.Subresource,
    };

    private static void ConfigureSettings(CoreWebView2Settings settings)
    {
        settings.AreDefaultContextMenusEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsZoomControlEnabled = false;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
