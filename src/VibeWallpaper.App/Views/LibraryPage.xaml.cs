using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeWallpaper.App.Services;
using VibeWallpaper.App.ViewModels;

namespace VibeWallpaper.App.Views;

public sealed partial class LibraryPage : Page
{
    private bool _restoringNetworkToggle;

    public LibraryPage(LibraryViewModel viewModel)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        InitializeComponent();
        ViewModel.PropertyChanged += ViewModel_PropertyChanged;
        Unloaded += LibraryPage_Unloaded;
        RefreshNoticeSeverity();
    }

    public LibraryViewModel ViewModel { get; }

    private async void ImportVideoButton_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ImportVideosAsync(CancellationToken.None); }
        finally { RefreshNoticeSeverity(); }
    }

    private async void ImportWebButton_Click(object sender, RoutedEventArgs e)
    {
        try { await ViewModel.ImportWebAsync(CancellationToken.None); }
        finally { RefreshNoticeSeverity(); }
    }

    private void UseWallpaperButton_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is { } item) ViewModel.UseWallpaper(item);
        RefreshNoticeSeverity();
    }

    private async void RevalidateButton_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        try { await ViewModel.RevalidateAsync(item, CancellationToken.None); }
        finally { RefreshNoticeSeverity(); }
    }

    private async void OpenLocationButton_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        try { await ViewModel.OpenSourceLocationAsync(item, CancellationToken.None); }
        finally { RefreshNoticeSeverity(); }
    }

    private async void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        if (ItemFrom(sender) is not { } item) return;
        try { await ViewModel.RemoveAsync(item, CancellationToken.None); }
        finally { RefreshNoticeSeverity(); }
    }

    private async void NetworkToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_restoringNetworkToggle || sender is not ToggleSwitch toggle || ItemFrom(sender) is not { } item) return;
        try
        {
            await ViewModel.SetNetworkPermissionAsync(item, toggle.IsOn, CancellationToken.None);
        }
        finally
        {
            var published = ViewModel.Items.FirstOrDefault(candidate => candidate.Id == item.Id);
            _restoringNetworkToggle = true;
            toggle.IsOn = published?.NetworkEnabled ?? item.NetworkEnabled;
            _restoringNetworkToggle = false;
            RefreshNoticeSeverity();
        }
    }

    private void LibraryNotice_CloseButtonClick(InfoBar sender, object args) =>
        ViewModel.DismissNotice();

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LibraryViewModel.Notice)) RefreshNoticeSeverity();
    }

    private void RefreshNoticeSeverity()
    {
        LibraryNotice.Severity = ViewModel.Notice.Severity switch
        {
            UserNoticeSeverity.Success => InfoBarSeverity.Success,
            UserNoticeSeverity.Warning => InfoBarSeverity.Warning,
            UserNoticeSeverity.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
    }

    private static LibraryItemViewModel? ItemFrom(object sender) =>
        (sender as FrameworkElement)?.DataContext as LibraryItemViewModel;

    private void LibraryPage_Unloaded(object sender, RoutedEventArgs e)
    {
        ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
        Unloaded -= LibraryPage_Unloaded;
    }
}
