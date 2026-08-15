#nullable enable
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeWallpaper.App.Services;
using VibeWallpaper.App.ViewModels;
using VibeWallpaper.App.Views;

namespace VibeWallpaper.App;

public sealed partial class MainWindow : Window, IManagementWindow
{
    private const int SwRestore = 9;
    private readonly ManagementWindowViewModel _viewModel;
    private LibraryViewModel? _libraryViewModel;
    private LibraryPage? _libraryPage;
    private bool _visible;

    public MainWindow(ManagementWindowViewModel viewModel)
        : this(viewModel, null)
    {
    }

    public MainWindow(ManagementWindowViewModel viewModel, LibraryViewModel? libraryViewModel)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        InitializeComponent();
        OutputList.ItemsSource = _viewModel.Outputs;
        WallpaperPicker.ItemsSource = _viewModel.Wallpapers;
        ModePicker.ItemsSource = _viewModel.DisplayModes;
        _viewModel.PropertyChanged += (_, _) => RefreshStatus();
        AppNavigation.SelectedItem = DisplaysNavigationItem;
        if (libraryViewModel is null)
        {
            LibraryNavigationItem.IsEnabled = false;
        }
        else
        {
            AttachLibrary(libraryViewModel);
        }
    }

    public nint Hwnd => WinRT.Interop.WindowNative.GetWindowHandle(this);
    public XamlRoot? DialogXamlRoot => AppNavigation.XamlRoot;

    public void AttachLibrary(LibraryViewModel libraryViewModel)
    {
        ArgumentNullException.ThrowIfNull(libraryViewModel);
        if (_libraryViewModel is not null)
            throw new InvalidOperationException("The library surface is already attached.");
        _libraryViewModel = libraryViewModel;
        _libraryPage = new LibraryPage(libraryViewModel);
        LibraryHost.Content = _libraryPage;
        LibraryNavigationItem.IsEnabled = true;
        _libraryViewModel.UseWallpaperRequested += LibraryViewModel_UseWallpaperRequested;
    }

    public bool IsVisible => _visible;
    public bool IsMinimized => AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter && presenter.State == Microsoft.UI.Windowing.OverlappedPresenterState.Minimized;

    public void Show()
    {
        Activate();
        _visible = true;
    }

    public void Hide()
    {
        AppWindow.Hide();
        _visible = false;
    }

    public void Restore() => _ = ShowWindow(WinRT.Interop.WindowNative.GetWindowHandle(this), SwRestore);

    public void BringToFront()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _ = SetForegroundWindow(hwnd);
    }

    public void LoadSnapshot(VibeWallpaper.Engine.Runtime.EngineSnapshot snapshot)
    {
        var selectedOutputKeys = OutputList.SelectedItems
            .OfType<ManagementOutputViewModel>()
            .Select(static output => output.Identity.Key)
            .ToHashSet(StringComparer.Ordinal);
        _viewModel.Load(snapshot);
        _libraryViewModel?.Replace(new LibrarySnapshot(
            0,
            snapshot.State.Library,
            snapshot.State.Assignments.Select(static assignment => assignment.Wallpaper).ToHashSet()));
        OutputList.SelectedItems.Clear();
        foreach (var output in _viewModel.Outputs.Where(output => selectedOutputKeys.Contains(output.Identity.Key)))
        {
            OutputList.SelectedItems.Add(output);
        }
        if (OutputList.SelectedItems.Count == 0 && _viewModel.Outputs.Count > 0)
        {
            OutputList.SelectedItems.Add(_viewModel.Outputs[0]);
        }
        WallpaperPicker.SelectedItem = _viewModel.SelectedWallpaper;
        ModePicker.SelectedItem = _viewModel.SelectedMode;
        RefreshStatus();
    }

    public void ShowCommandResult(string message, string? errorCode)
    {
        StatusText.Text = message;
        ErrorText.Text = string.IsNullOrWhiteSpace(errorCode)
            ? string.Empty
            : $"Mã chẩn đoán: {errorCode}";
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            ManagementNotice.IsOpen = false;
            return;
        }

        var isRestoreWarning = string.Equals(errorCode, "wallpaper.restore.skipped", StringComparison.Ordinal)
            || string.Equals(errorCode, "wallpaper.fallback.activation_failed", StringComparison.Ordinal);
        var presentation = UserErrorPresenter.Create(errorCode, message);
        ManagementNotice.Title = isRestoreWarning
            ? "Một wallpaper chưa được khôi phục"
            : presentation.Title;
        ManagementNotice.Message = presentation.DetailedMessage;
        ManagementNotice.Severity = isRestoreWarning
            ? InfoBarSeverity.Warning
            : InfoBarSeverity.Error;
        ManagementNotice.IsOpen = true;
    }

    public void AttachController(ManagementWindowController controller)
    {
        AppWindow.Closing += (_, args) => args.Cancel = controller.HandleClosing();
    }

    public event Action? ExitRequested;

    private void OutputList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SelectedOutput = OutputList.SelectedItem as ManagementOutputViewModel;

    private void WallpaperPicker_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _viewModel.SelectedWallpaper = WallpaperPicker.SelectedItem as ManagementWallpaperViewModel;

    private void ModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ModePicker.SelectedItem is VibeWallpaper.Engine.Core.Wallpapers.DisplayMode mode)
        {
            _viewModel.SelectedMode = mode;
        }
    }

    private void AppNavigation_SelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var showLibrary = ReferenceEquals(args.SelectedItem, LibraryNavigationItem)
            && _libraryPage is not null;
        DisplaysHost.Visibility = showLibrary ? Visibility.Collapsed : Visibility.Visible;
        LibraryHost.Visibility = showLibrary ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LibraryViewModel_UseWallpaperRequested(
        VibeWallpaper.Engine.Core.Wallpapers.WallpaperId id)
    {
        if (!_viewModel.SelectWallpaper(id)) return;
        WallpaperPicker.SelectedItem = _viewModel.SelectedWallpaper;
        AppNavigation.SelectedItem = DisplaysNavigationItem;
        DisplaysHost.Visibility = Visibility.Visible;
        LibraryHost.Visibility = Visibility.Collapsed;
        _ = OutputList.Focus(FocusState.Programmatic) || ApplyWallpaperButton.Focus(FocusState.Programmatic);
    }

    private async void ApplyWallpaperButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedOutputs = OutputList.SelectedItems
            .OfType<ManagementOutputViewModel>()
            .Select(output => output.Identity)
            .ToArray();
        ApplyWallpaperButton.IsEnabled = false;
        try
        {
            await _viewModel.ApplyWallpaperAsync(selectedOutputs, CancellationToken.None);
        }
        finally
        {
            ApplyWallpaperButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        var selectedOutputs = OutputList.SelectedItems
            .OfType<ManagementOutputViewModel>()
            .Select(output => output.Identity)
            .ToArray();
        _viewModel.Color = ColorText.Text;
        ApplyButton.IsEnabled = false;
        try
        {
            await _viewModel.ApplyColorAsync(selectedOutputs, CancellationToken.None);
        }
        finally
        {
            ApplyButton.IsEnabled = true;
            RefreshStatus();
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => ExitRequested?.Invoke();

    private void RefreshStatus()
    {
        ShowCommandResult(_viewModel.StatusMessage, _viewModel.ErrorCode);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
}
