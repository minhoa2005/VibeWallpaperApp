namespace VibeWallpaper.Engine.Rendering.Web;

public sealed class WebEnvironmentProvider : IWebEnvironmentProvider
{
    private readonly string _userDataFolder;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private WebEnvironmentHandle? _environment;
    private long _generation = 1;
    private bool _disposed;

    public WebEnvironmentProvider(string userDataFolder)
    {
        if (string.IsNullOrWhiteSpace(userDataFolder) || !Path.IsPathFullyQualified(userDataFolder))
        {
            throw new ArgumentException("An absolute WebView2 user-data folder is required.", nameof(userDataFolder));
        }

        _userDataFolder = Path.GetFullPath(userDataFolder);
    }

    public long Generation => Interlocked.Read(ref _generation);

    public async Task<WebEnvironmentHandle> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (_environment is not null)
            {
                return _environment;
            }

            Directory.CreateDirectory(_userDataFolder);
            _environment = new WebEnvironmentHandle(Generation, _userDataFolder);
            return _environment;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task InvalidateAsync(long expectedGeneration, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfDisposed();
            if (expectedGeneration != Generation)
            {
                return;
            }

            _environment = null;
            Interlocked.Increment(ref _generation);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            _environment = null;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(WebEnvironmentProvider));
        }
    }
}
