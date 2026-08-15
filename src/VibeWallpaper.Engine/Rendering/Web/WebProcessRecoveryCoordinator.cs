using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Wallpapers;

namespace VibeWallpaper.Engine.Rendering.Web;

public enum WebFailureScope
{
    AffectedRenderer,
    AuxiliaryProcess,
    SharedBrowserProcess,
}

public sealed record WebProcessFailure(
    WebFailureScope Scope,
    RendererInstanceId? RendererInstance,
    MonitorIdentity? Output,
    string ProcessFailedKind);

public sealed record WebRendererRegistration(
    RendererInstanceId RendererInstance,
    WallpaperId Wallpaper,
    MonitorIdentity Output,
    long EnvironmentGeneration,
    long AssignmentGeneration);

public interface IWebRendererRegistry
{
    IReadOnlyList<WebRendererRegistration> SnapshotActive();
    Task RecreateAsync(RendererInstanceId rendererInstance, CancellationToken cancellationToken);
    Task RecreateAllAsync(long environmentGeneration, CancellationToken cancellationToken);
}

public sealed class WebProcessRecoveryCoordinator
{
    private readonly IWebEnvironmentProvider _environment;
    private readonly IWebRendererRegistry _registry;
    private readonly SemaphoreSlim _sharedGate = new(1, 1);
    private long _lastSharedRecoveryGeneration = -1;
    private DateTimeOffset _lastSharedRecoveryUtc;

    public WebProcessRecoveryCoordinator(IWebEnvironmentProvider environment, IWebRendererRegistry registry)
    {
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async Task HandleAsync(WebProcessFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (!Enum.IsDefined(failure.Scope)) throw new ArgumentException("A defined failure scope is required.", nameof(failure));
        if (string.IsNullOrWhiteSpace(failure.ProcessFailedKind)) throw new ArgumentException("Failure kind is required.", nameof(failure));

        switch (failure.Scope)
        {
            case WebFailureScope.AffectedRenderer:
                if (!failure.RendererInstance.HasValue) throw new ArgumentException("Renderer failures require an instance ID.", nameof(failure));
                await _registry.RecreateAsync(failure.RendererInstance.Value, cancellationToken).ConfigureAwait(false);
                break;
            case WebFailureScope.AuxiliaryProcess:
                // WebView2 owns automatic GPU/utility recovery; do not recreate all renderers.
                break;
            case WebFailureScope.SharedBrowserProcess:
                await RecoverSharedAsync(_environment.Generation, cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task RecoverSharedAsync(long observedGeneration, CancellationToken cancellationToken)
    {
        await _sharedGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lastSharedRecoveryGeneration == observedGeneration)
            {
                return;
            }

            if (_lastSharedRecoveryUtc != default
                && DateTimeOffset.UtcNow - _lastSharedRecoveryUtc < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            await _environment.InvalidateAsync(observedGeneration, cancellationToken).ConfigureAwait(false);
            _lastSharedRecoveryGeneration = observedGeneration;
            _lastSharedRecoveryUtc = DateTimeOffset.UtcNow;
            await _registry.RecreateAllAsync(_environment.Generation, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sharedGate.Release();
        }
    }
}
