using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Rendering.Web;

namespace VibeWallpaper.Tests.Rendering;

public sealed class WebSecurityPolicyTests
{
    private readonly WallpaperId _wallpaper = WallpaperId.New();
    private readonly WebSecurityPolicy _policy = new();

    [Theory]
    [InlineData("file:///C:/secret.txt")]
    [InlineData("vibe-native://command")]
    [InlineData("ftp://example.test/file")]
    public void PrivilegedOrCustomScheme_IsAlwaysBlocked(string uri)
    {
        var content = new WebContentPolicy(
            _wallpaper,
            @"C:\Wallpapers\Demo",
            WebWallpaperOrigin.Create(_wallpaper),
            networkEnabled: true);

        Assert.Equal(
            WebRequestDecision.Block,
            _policy.Decide(content, new Uri(uri), WebResourceKind.Subresource));
    }

    [Fact]
    public void ExternalFetch_IsBlockedByDefault_AndAllowedOnlyWhenEnabled()
    {
        var uri = new Uri("https://example.test/data.json");
        var disabled = CreateContent(networkEnabled: false);
        var enabled = CreateContent(networkEnabled: true);

        Assert.Equal(WebRequestDecision.Block,
            _policy.Decide(disabled, uri, WebResourceKind.Fetch));
        Assert.Equal(WebRequestDecision.AllowExternalNetwork,
            _policy.Decide(enabled, uri, WebResourceKind.Fetch));
    }

    [Fact]
    public void MappedOrigin_IsAllowedOnlyForTheCurrentWallpaper()
    {
        var content = CreateContent(networkEnabled: false);
        var otherOrigin = WebWallpaperOrigin.Create(WallpaperId.New());

        Assert.Equal(WebRequestDecision.AllowMappedContent,
            _policy.Decide(content, content.Origin, WebResourceKind.Navigation));
        Assert.Equal(WebRequestDecision.Block,
            _policy.Decide(content, otherOrigin, WebResourceKind.Navigation));
    }

    [Theory]
    [InlineData("data:text/plain,ok")]
    [InlineData("blob:https://wallpaper.example/entry")]
    public void InlineContent_IsBlockedForTopLevelNavigation(string uri)
    {
        var content = CreateContent(networkEnabled: true);

        Assert.Equal(WebRequestDecision.Block,
            _policy.Decide(content, new Uri(uri), WebResourceKind.Navigation));
    }

    [Fact]
    public void WebSocket_IsAllowedOnlyWhenNetworkIsEnabled()
    {
        var uri = new Uri("wss://example.test/socket");

        Assert.Equal(WebRequestDecision.Block,
            _policy.Decide(CreateContent(networkEnabled: false), uri, WebResourceKind.WebSocket));
        Assert.Equal(WebRequestDecision.AllowExternalNetwork,
            _policy.Decide(CreateContent(networkEnabled: true), uri, WebResourceKind.WebSocket));
    }

    [Fact]
    public void MappedOrigin_RequiresTheRootPathAndRejectsTraversal()
    {
        var content = CreateContent(networkEnabled: false);

        Assert.True(content.IsPathAllowed(Path.Combine(content.CanonicalRoot, "index.html")));
        Assert.False(content.IsPathAllowed(Path.Combine(content.CanonicalRoot, "..", "secret.txt")));
        Assert.False(content.IsPathAllowed(Path.Combine(content.CanonicalRoot, "missing", "..", "..", "secret.txt")));
    }

    [Fact]
    public void Policy_BlocksMappedOriginDownloadsPopupsAndPermissions()
    {
        var content = CreateContent(networkEnabled: true);

        Assert.Equal(WebRequestDecision.Block,
            _policy.Decide(content, content.Origin, WebResourceKind.Download));
        Assert.Equal(WebRequestDecision.Block,
            _policy.Decide(content, content.Origin, WebResourceKind.Popup));
        Assert.Equal(WebRequestDecision.Block,
            _policy.Decide(content, content.Origin, WebResourceKind.Permission));
    }

    private WebContentPolicy CreateContent(bool networkEnabled) => new(
        _wallpaper,
        @"C:\Wallpapers\Demo",
        WebWallpaperOrigin.Create(_wallpaper),
        networkEnabled);
}
