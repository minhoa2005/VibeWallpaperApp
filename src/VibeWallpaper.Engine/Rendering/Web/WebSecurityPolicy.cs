using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Rendering.Web;

public enum WebRequestDecision
{
    AllowMappedContent,
    AllowExternalNetwork,
    Block,
}

public enum WebResourceKind
{
    Navigation,
    Subresource,
    Fetch,
    WebSocket,
    ServiceWorker,
    Download,
    Popup,
    Permission,
}

public sealed record WebContentPolicy
{
    public WebContentPolicy(
        WallpaperId wallpaper,
        string canonicalRoot,
        Uri origin,
        bool networkEnabled)
    {
        if (wallpaper.Value == Guid.Empty)
        {
            throw new ArgumentException("A wallpaper ID is required.", nameof(wallpaper));
        }

        if (string.IsNullOrWhiteSpace(canonicalRoot) || !Path.IsPathFullyQualified(canonicalRoot))
        {
            throw new ArgumentException("The content root must be an absolute path.", nameof(canonicalRoot));
        }

        ArgumentNullException.ThrowIfNull(origin);
        if (!string.Equals(origin.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || !origin.AbsolutePath.Equals("/", StringComparison.Ordinal))
        {
            throw new ArgumentException("The mapped origin must be an HTTPS origin.", nameof(origin));
        }

        Wallpaper = wallpaper;
        CanonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonicalRoot));
        Origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);
        NetworkEnabled = networkEnabled;
    }

    public WallpaperId Wallpaper { get; }
    public string CanonicalRoot { get; }
    public Uri Origin { get; }
    public bool NetworkEnabled { get; }

    public bool IsPathAllowed(string candidatePath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || !Path.IsPathFullyQualified(candidatePath))
        {
            return false;
        }

        string candidate;
        try
        {
            candidate = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidatePath));
        }
        catch (ArgumentException)
        {
            return false;
        }

        var rootPrefix = CanonicalRoot + Path.DirectorySeparatorChar;
        if (!candidate.Equals(CanonicalRoot, StringComparison.OrdinalIgnoreCase)
            && !candidate.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // Existing reparse points are not trusted as content roots because they
        // can redirect a mapped path outside the canonical tree.
        var current = new DirectoryInfo(CanonicalRoot);
        if (current.Exists && HasReparsePoint(current))
        {
            return false;
        }

        var relative = Path.GetRelativePath(CanonicalRoot, candidate);
        if (relative == ".")
        {
            return true;
        }

        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (string.IsNullOrEmpty(segment))
            {
                continue;
            }

            current = new DirectoryInfo(Path.Combine(current.FullName, segment));
            if (current.Exists && HasReparsePoint(current))
            {
                return false;
            }
        }

        var file = new FileInfo(candidate);
        return !file.Exists || !HasReparsePoint(file);
    }

    private static bool HasReparsePoint(FileSystemInfo info) =>
        (info.Attributes & FileAttributes.ReparsePoint) != 0;
}

public sealed class WebSecurityPolicy
{
    public WebRequestDecision Decide(
        WebContentPolicy content,
        Uri request,
        WebResourceKind resourceKind)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(request);
        if (!request.IsAbsoluteUri || !Enum.IsDefined(resourceKind))
        {
            return WebRequestDecision.Block;
        }

        if (IsMappedOrigin(content.Origin, request))
        {
            return resourceKind is WebResourceKind.Popup
                or WebResourceKind.Download
                or WebResourceKind.Permission
                ? WebRequestDecision.Block
                : WebRequestDecision.AllowMappedContent;
        }

        if (resourceKind is WebResourceKind.Navigation
            or WebResourceKind.Popup
            or WebResourceKind.Download
            or WebResourceKind.Permission)
        {
            return WebRequestDecision.Block;
        }

        if (request.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || request.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || request.Scheme.Equals(Uri.UriSchemeWs, StringComparison.OrdinalIgnoreCase)
            || request.Scheme.Equals(Uri.UriSchemeWss, StringComparison.OrdinalIgnoreCase))
        {
            return content.NetworkEnabled
                ? WebRequestDecision.AllowExternalNetwork
                : WebRequestDecision.Block;
        }

        // file:, data:, blob:, javascript:, and all custom schemes are never
        // treated as trusted content unless they belong to the mapped origin.
        return WebRequestDecision.Block;
    }

    private static bool IsMappedOrigin(Uri origin, Uri request) =>
        string.Equals(origin.Scheme, request.Scheme, StringComparison.OrdinalIgnoreCase)
        && string.Equals(origin.Host, request.Host, StringComparison.OrdinalIgnoreCase)
        && origin.Port == request.Port;
}
