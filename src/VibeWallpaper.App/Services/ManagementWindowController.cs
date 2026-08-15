#nullable enable
namespace VibeWallpaper.App.Services;

public interface IManagementWindow
{
    bool IsVisible { get; }
    bool IsMinimized { get; }
    void Show();
    void Hide();
    void Restore();
    void BringToFront();
}

public sealed class ManagementWindowController
{
    private readonly IManagementWindow _window;
    private readonly TrayIconService _tray;
    private bool _closePermitted;

    public ManagementWindowController(IManagementWindow window, TrayIconService tray)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(tray);
        _window = window;
        _tray = tray;
        _tray.OpenRequested += Open;
        _tray.AvailabilityChanged += HandleTrayAvailabilityChanged;
    }

    public bool HandleClosing()
    {
        if (_closePermitted)
        {
            return false;
        }

        if (_tray.IsAvailable)
        {
            _window.Hide();
        }

        return true;
    }

    public void PermitClose() => _closePermitted = true;

    private void HandleTrayAvailabilityChanged(bool available, string? error)
    {
        if (!available)
        {
            Open();
        }
    }

    public void Open()
    {
        if (_window.IsMinimized)
        {
            _window.Restore();
        }

        if (!_window.IsVisible)
        {
            _window.Show();
        }

        _window.BringToFront();
    }
}
