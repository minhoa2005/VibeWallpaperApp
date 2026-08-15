#nullable enable
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.App.ViewModels;

public interface IManagementWallpaperCommands
{
    Task ApplyColorAsync(
        string color,
        DisplayMode mode,
        IReadOnlyList<MonitorIdentity> outputs,
        CancellationToken cancellationToken);
    Task ApplyWallpaperAsync(
        WallpaperId wallpaper,
        DisplayMode mode,
        IReadOnlyList<MonitorIdentity> outputs,
        CancellationToken cancellationToken);
}

public sealed class WallpaperCommandException : Exception
{
    public WallpaperCommandException(string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    public string Code { get; }
}

public sealed record ManagementOutputViewModel(
    MonitorIdentity Identity,
    string DisplayName,
    string Status);

public sealed record ManagementWallpaperViewModel(
    WallpaperId Id,
    string Name,
    WallpaperKind Kind,
    SourceValidationStatus ValidationStatus);

public sealed class ManagementWindowViewModel : INotifyPropertyChanged
{
    private readonly IManagementWallpaperCommands _commands;
    private ManagementOutputViewModel? _selectedOutput;
    private ManagementWallpaperViewModel? _selectedWallpaper;
    private DisplayMode _selectedMode = DisplayMode.Independent;
    private string _color = "#101014";
    private string _statusMessage = "Ready";
    private string? _errorCode;
    private bool _isBusy;

    public ManagementWindowViewModel(IManagementWallpaperCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        _commands = commands;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ManagementOutputViewModel> Outputs { get; } = [];
    public ObservableCollection<ManagementWallpaperViewModel> Wallpapers { get; } = [];
    public IReadOnlyList<DisplayMode> DisplayModes { get; } =
        [DisplayMode.Independent, DisplayMode.Duplicate, DisplayMode.Span];

    public ManagementOutputViewModel? SelectedOutput
    {
        get => _selectedOutput;
        set => SetField(ref _selectedOutput, value);
    }

    public string Color
    {
        get => _color;
        set => SetField(ref _color, value);
    }

    public ManagementWallpaperViewModel? SelectedWallpaper
    {
        get => _selectedWallpaper;
        set => SetField(ref _selectedWallpaper, value);
    }

    public DisplayMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Enum.IsDefined(value)) throw new ArgumentException("A defined display mode is required.", nameof(value));
            SetField(ref _selectedMode, value);
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string? ErrorCode
    {
        get => _errorCode;
        private set => SetField(ref _errorCode, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetField(ref _isBusy, value);
    }

    public void Load(EngineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var selectedOutputId = SelectedOutput?.Identity;
        var selectedWallpaperId = SelectedWallpaper?.Id;
        Outputs.Clear();
        foreach (var output in snapshot.Outputs)
        {
            var effective = output.EffectiveState;
            var status = effective?.EffectiveKind == EffectiveWallpaperKind.SolidFallback
                ? $"Solid fallback ({effective.FallbackReasonCode ?? "configured"})"
                : output.Lifecycle?.ToString() ?? "Ready";
            Outputs.Add(new ManagementOutputViewModel(output.Output, output.Output.Key, status));
        }

        SelectedOutput = Outputs.FirstOrDefault(item => item.Identity == selectedOutputId)
            ?? Outputs.FirstOrDefault();
        Wallpapers.Clear();
        foreach (var item in snapshot.State.Library)
        {
            Wallpapers.Add(new ManagementWallpaperViewModel(
                item.Definition.Id,
                item.Definition.Name,
                item.Definition.Source.Kind,
                item.Validation.Status));
        }

        SelectedWallpaper = Wallpapers.FirstOrDefault(item => item.Id == selectedWallpaperId)
            ?? Wallpapers.FirstOrDefault();
    }

    public bool SelectWallpaper(WallpaperId id)
    {
        var item = Wallpapers.FirstOrDefault(candidate => candidate.Id == id);
        if (item is null) return false;
        SelectedWallpaper = item;
        return true;
    }

    public async Task ApplyColorAsync(
        IReadOnlyList<MonitorIdentity> selectedOutputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedOutputs);
        if (IsBusy) return;
        var uniqueOutputs = selectedOutputs.Distinct().ToArray();
        if (uniqueOutputs.Length == 0)
        {
            SetError("wallpaper.output.required", "Hãy chọn ít nhất một màn hình.");
            return;
        }

        if (SelectedMode == DisplayMode.Independent && uniqueOutputs.Length != 1)
        {
            SetError("wallpaper.output.independent_count", "Chế độ Independent yêu cầu đúng một màn hình.");
            return;
        }

        if (SelectedMode is DisplayMode.Duplicate or DisplayMode.Span && uniqueOutputs.Length < 2)
        {
            SetError("wallpaper.output.group_count", "Chế độ Duplicate và Span yêu cầu ít nhất hai màn hình.");
            return;
        }

        string normalized;
        try
        {
            normalized = SolidColorSource.Create(Color).HexColor;
        }
        catch (ArgumentException)
        {
            SetError("wallpaper.color.invalid", "Enter a color in #RRGGBB format.");
            return;
        }

        IsBusy = true;
        ErrorCode = null;
        try
        {
            await _commands.ApplyColorAsync(normalized, SelectedMode, uniqueOutputs, cancellationToken);
            StatusMessage = $"Đã đặt màu {normalized} ở chế độ {SelectedMode} cho {uniqueOutputs.Length} màn hình.";
        }
        catch (WallpaperCommandException exception)
        {
            SetError(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorCode = null;
            StatusMessage = "Đã hủy thao tác.";
        }
        catch
        {
            SetError("wallpaper.apply.failed", "Không thể đặt màu nền.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    public async Task ApplyWallpaperAsync(
        IReadOnlyList<MonitorIdentity> selectedOutputs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedOutputs);
        if (IsBusy) return;
        if (SelectedWallpaper is null)
        {
            SetError("wallpaper.library.required", "Select a wallpaper first.");
            return;
        }

        var uniqueOutputs = selectedOutputs.Distinct().ToArray();
        if (uniqueOutputs.Length == 0)
        {
            SetError("wallpaper.output.required", "Select at least one output.");
            return;
        }

        if (SelectedMode == DisplayMode.Independent && uniqueOutputs.Length != 1)
        {
            SetError("wallpaper.output.independent_count", "Independent mode requires exactly one output.");
            return;
        }

        if (SelectedMode is DisplayMode.Duplicate or DisplayMode.Span && uniqueOutputs.Length < 2)
        {
            SetError("wallpaper.output.group_count", "Duplicate and Span modes require at least two outputs.");
            return;
        }

        IsBusy = true;
        ErrorCode = null;
        try
        {
            await _commands.ApplyWallpaperAsync(
                SelectedWallpaper.Id,
                SelectedMode,
                uniqueOutputs,
                cancellationToken);
            StatusMessage = $"Applied {SelectedWallpaper.Name} in {SelectedMode} mode to {uniqueOutputs.Length} outputs.";
        }
        catch (WallpaperCommandException exception)
        {
            SetError(exception.Code, exception.Message);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ErrorCode = null;
            StatusMessage = "Đã hủy thao tác.";
        }
        catch
        {
            SetError("wallpaper.apply.failed", "Không thể đặt wallpaper.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void SetError(string code, string message)
    {
        ErrorCode = code;
        StatusMessage = message;
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
