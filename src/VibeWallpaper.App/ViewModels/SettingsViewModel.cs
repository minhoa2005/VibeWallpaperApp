using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeWallpaper.App.Services;
using VibeWallpaper.Engine.Core.Persistence;

namespace VibeWallpaper.App.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly ISettingsController _controller;
    private string _interactionHotkey;
    private bool _hasHotkeyConflict;

    public SettingsViewModel(AppSettings settings, ISettingsController controller)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));
        _interactionHotkey = settings.InteractionHotkey;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public string InteractionHotkey { get => _interactionHotkey; private set => SetField(ref _interactionHotkey, value); }
    public bool HasHotkeyConflict { get => _hasHotkeyConflict; private set => SetField(ref _hasHotkeyConflict, value); }

    public async Task ChangeHotkeyAsync(string gesture, CancellationToken cancellationToken)
    {
        var result = await _controller.ChangeHotkeyAsync(gesture, cancellationToken).ConfigureAwait(false);
        HasHotkeyConflict = result.Status == HotkeyChangeStatus.Conflict;
        if (result.Status == HotkeyChangeStatus.Applied)
        {
            InteractionHotkey = result.EffectiveGesture;
        }
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
