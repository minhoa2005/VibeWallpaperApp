namespace VibeWallpaper.Engine.Rendering.Web;

public interface IWebPolicyTarget
{
    void Attach(Func<Uri, WebResourceKind, WebRequestDecision> decide);
    void Detach();
}

public sealed class WebPolicySession : IAsyncDisposable
{
    private readonly IWebPolicyTarget _target;
    private readonly WebContentPolicy _policy;
    private readonly WebSecurityPolicy _securityPolicy;
    private bool _disposed;

    public WebPolicySession(
        IWebPolicyTarget target,
        WebContentPolicy policy,
        WebSecurityPolicy? securityPolicy = null)
    {
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _securityPolicy = securityPolicy ?? new WebSecurityPolicy();
    }

    public void Attach()
    {
        ThrowIfDisposed();
        _target.Attach((uri, kind) => _securityPolicy.Decide(_policy, uri, kind));
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _disposed = true;
        _target.Detach();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WebPolicySession));
        }
    }
}
