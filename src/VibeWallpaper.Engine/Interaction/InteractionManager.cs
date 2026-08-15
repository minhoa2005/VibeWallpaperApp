using VibeWallpaper.Engine.Core.Monitors;

namespace VibeWallpaper.Engine.Interaction;

public sealed class InteractionManager
{
    private readonly IInteractionOverlayFactory _overlayFactory;
    private readonly IReadOnlyList<MonitorIdentity> _outputs;
    private readonly bool _desktopContextAvailable;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly List<IInteractionOverlay> _overlays = [];
    private bool _disposed;

    public InteractionManager(
        IInteractionOverlayFactory overlayFactory,
        IReadOnlyList<MonitorIdentity> outputs,
        bool desktopContextAvailable = true)
    {
        _overlayFactory = overlayFactory ?? throw new ArgumentNullException(nameof(overlayFactory));
        _outputs = outputs ?? throw new ArgumentNullException(nameof(outputs));
        if (_outputs.Any(static output => output is null)) throw new ArgumentException("Outputs cannot contain null.", nameof(outputs));
        _desktopContextAvailable = desktopContextAvailable;
    }

    public bool IsActive { get; private set; }

    public async Task<bool> EnterAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (IsActive || !_desktopContextAvailable || _outputs.Count == 0)
            {
                return false;
            }

            try
            {
                foreach (var output in _outputs)
                {
                    _overlays.Add(_overlayFactory.Create(output));
                }

                IsActive = true;
                return true;
            }
            catch
            {
                DestroyOverlays();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ExitAsync(InteractionExitReason reason, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentException("A defined exit reason is required.", nameof(reason));
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsActive && _overlays.Count == 0)
            {
                return;
            }

            try
            {
                IsActive = false;
            }
            finally
            {
                DestroyOverlays();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await ExitAsync(InteractionExitReason.ApplicationExit, CancellationToken.None).ConfigureAwait(false);
        _disposed = true;
        _gate.Dispose();
    }

    private void DestroyOverlays()
    {
        foreach (var overlay in _overlays)
        {
            try { overlay.Destroy(); } catch { }
        }
        _overlays.Clear();
    }
}
