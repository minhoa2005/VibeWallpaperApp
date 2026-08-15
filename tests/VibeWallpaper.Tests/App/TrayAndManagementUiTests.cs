using VibeWallpaper.App.Services;
using VibeWallpaper.App.ViewModels;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Runtime;

namespace VibeWallpaper.Tests.App;

public sealed class TrayAndManagementUiTests
{
    [Fact]
    public void TryStart_WhenBackendReturnsFalse_ExposesActionableError()
    {
        using var tray = new TrayIconService(new RecordingTrayBackend(succeeds: false));

        Assert.False(tray.TryStart());

        Assert.Equal("The notification-area icon could not be created.", tray.LastError);
    }

    [Fact]
    public void TryStart_WhenBackendThrows_ExposesOperationAndException()
    {
        using var tray = new TrayIconService(new ThrowingTrayBackend());

        Assert.False(tray.TryStart());

        Assert.Contains("Shell_NotifyIcon(NIM_ADD)", tray.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public void TrayRecoveryFailure_MarksIconUnavailableAndKeepsWindowReachable()
    {
        var backend = new RecordingTrayBackend(succeeds: true);
        using var tray = new TrayIconService(backend);
        Assert.True(tray.TryStart());
        var window = new RecordingManagementWindow();
        var controller = new ManagementWindowController(window, tray);

        backend.PublishAvailability(false, "Shell_NotifyIcon recovery failed.");
        var cancelClose = controller.HandleClosing();

        Assert.False(tray.IsAvailable);
        Assert.Equal("Shell_NotifyIcon recovery failed.", tray.LastError);
        Assert.True(cancelClose);
        Assert.Equal(0, window.HideCount);
        Assert.True(window.IsVisible);
    }

    [Fact]
    public void LaterTrayRecoverySuccess_RestoresAvailabilityAndClearsError()
    {
        var backend = new RecordingTrayBackend(succeeds: true);
        using var tray = new TrayIconService(backend);
        Assert.True(tray.TryStart());
        backend.PublishAvailability(false, "Shell_NotifyIcon recovery failed.");

        backend.PublishAvailability(true, null);

        Assert.True(tray.IsAvailable);
        Assert.Null(tray.LastError);
    }

    [Fact]
    public void TrayRecoveryFailure_WhenWindowWasHidden_ReopensWindow()
    {
        var backend = new RecordingTrayBackend(succeeds: true);
        using var tray = new TrayIconService(backend);
        Assert.True(tray.TryStart());
        var window = new RecordingManagementWindow { IsVisible = false };
        _ = new ManagementWindowController(window, tray);

        backend.PublishAvailability(false, "Shell_NotifyIcon recovery failed.");

        Assert.True(window.IsVisible);
        Assert.Equal(1, window.BringToFrontCount);
    }

    [Fact]
    public void LaterTrayRecoverySuccess_SynchronizesPauseStateChangedWhileUnavailable()
    {
        var backend = new RecordingTrayBackend(succeeds: true);
        using var tray = new TrayIconService(backend);
        Assert.True(tray.TryStart());
        backend.PublishAvailability(false, "Shell_NotifyIcon recovery failed.");
        tray.SetPaused(true);

        backend.PublishAvailability(true, null);

        Assert.True(backend.LastPaused);
    }

    [Fact]
    public void ClosingWindow_WithWorkingTray_HidesWindowAndCancelsClose()
    {
        var backend = new RecordingTrayBackend(succeeds: true);
        using var tray = new TrayIconService(backend);
        Assert.True(tray.TryStart());
        var window = new RecordingManagementWindow();
        var controller = new ManagementWindowController(window, tray);

        var cancelClose = controller.HandleClosing();

        Assert.True(cancelClose);
        Assert.Equal(1, window.HideCount);
    }

    [Fact]
    public void ClosingWindow_WhenTrayCreationFails_CancelsCloseAndLeavesWindowVisibleAndReachable()
    {
        using var tray = new TrayIconService(new RecordingTrayBackend(succeeds: false));
        Assert.False(tray.TryStart());
        var window = new RecordingManagementWindow();
        var controller = new ManagementWindowController(window, tray);

        var cancelClose = controller.HandleClosing();

        Assert.True(cancelClose);
        Assert.Equal(0, window.HideCount);
        Assert.True(window.IsVisible);
    }

    [Fact]
    public void ClosingWindow_AfterCoordinatedExitIsPermitted_ClosesInsteadOfHiding()
    {
        using var tray = new TrayIconService(new RecordingTrayBackend(succeeds: true));
        Assert.True(tray.TryStart());
        var window = new RecordingManagementWindow();
        var controller = new ManagementWindowController(window, tray);

        controller.PermitClose();

        Assert.False(controller.HandleClosing());
        Assert.Equal(0, window.HideCount);
    }

    [Fact]
    public void TrayOpen_RestoresShowsAndBringsManagementWindowForward()
    {
        var backend = new RecordingTrayBackend(succeeds: true);
        using var tray = new TrayIconService(backend);
        Assert.True(tray.TryStart());
        var window = new RecordingManagementWindow { IsMinimized = true, IsVisible = false };
        _ = new ManagementWindowController(window, tray);

        backend.Menu!.Open();

        Assert.False(window.IsMinimized);
        Assert.True(window.IsVisible);
        Assert.Equal(1, window.BringToFrontCount);
    }

    [Fact]
    public async Task ApplyColorAsync_DuplicateForwardsAllSelectedOutputsAndReportsSuccess()
    {
        var commands = new RecordingCommands();
        var viewModel = new ManagementWindowViewModel(commands);
        var first = new MonitorIdentity("DISPLAY-A");
        var second = new MonitorIdentity("DISPLAY-B");
        viewModel.Load(new EngineSnapshot(
            PersistedState.Default,
            [Output(first), Output(second)]));
        viewModel.SelectedMode = DisplayMode.Duplicate;
        viewModel.Color = "#12aBcD";

        await viewModel.ApplyColorAsync([first, second], TestContext.Current.CancellationToken);

        var applied = Assert.Single(commands.Applied);
        Assert.Equal(DisplayMode.Duplicate, applied.Mode);
        Assert.Equal([first, second], applied.Outputs);
        Assert.Equal("#12ABCD", applied.Color);
        Assert.Contains("2", viewModel.StatusMessage, StringComparison.Ordinal);
        Assert.Null(viewModel.ErrorCode);
    }

    [Fact]
    public async Task ApplyColorAsync_WhenCommandFails_ShowsTypedErrorCode()
    {
        var commands = new RecordingCommands
        {
            Failure = new WallpaperCommandException("wallpaper.host.unavailable", "Desktop host unavailable."),
        };
        var viewModel = new ManagementWindowViewModel(commands);
        var output = new MonitorIdentity("DISPLAY-A");
        viewModel.Load(new EngineSnapshot(PersistedState.Default, [Output(output)]));
        viewModel.SelectedOutput = viewModel.Outputs.Single();
        viewModel.Color = "#445566";

        await viewModel.ApplyColorAsync([output], TestContext.Current.CancellationToken);

        Assert.Equal("wallpaper.host.unavailable", viewModel.ErrorCode);
        Assert.Equal("Desktop host unavailable.", viewModel.StatusMessage);
    }

    [Fact]
    public void Load_PopulatesAssignableWallpapersFromPersistedLibrary()
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(), "Aurora", SolidColorSource.Create("#123456"),
            FitMode.Cover, 30, false, false, 0, false);
        var item = new WallpaperLibraryItem(
            definition, null, null,
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UtcNow));
        var state = new PersistedState(1, [item], [], [], null);
        var viewModel = new ManagementWindowViewModel(new RecordingCommands());

        viewModel.Load(new EngineSnapshot(state, [Output(new MonitorIdentity("DISPLAY-A"))]));

        var wallpaper = Assert.Single(viewModel.Wallpapers);
        Assert.Equal(definition.Id, wallpaper.Id);
        Assert.Equal("Aurora", wallpaper.Name);
        Assert.Equal(definition.Source.Kind, wallpaper.Kind);
        Assert.Same(wallpaper, viewModel.SelectedWallpaper);
    }

    [Fact]
    public async Task ApplyWallpaperAsync_ForwardsWallpaperModeAndAllSelectedOutputs()
    {
        var commands = new RecordingCommands();
        var definition = new WallpaperDefinition(
            WallpaperId.New(), "Grouped", SolidColorSource.Create("#234567"),
            FitMode.Cover, 30, false, false, 0, false);
        var item = new WallpaperLibraryItem(
            definition, null, null,
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UtcNow));
        var first = new MonitorIdentity("DISPLAY-A");
        var second = new MonitorIdentity("DISPLAY-B");
        var viewModel = new ManagementWindowViewModel(commands);
        viewModel.Load(new EngineSnapshot(new PersistedState(1, [item], [], [], null), [Output(first), Output(second)]));
        viewModel.SelectedMode = DisplayMode.Span;

        await viewModel.ApplyWallpaperAsync([first, second], TestContext.Current.CancellationToken);

        var applied = Assert.Single(commands.WallpapersApplied);
        Assert.Equal(definition.Id, applied.Wallpaper);
        Assert.Equal(DisplayMode.Span, applied.Mode);
        Assert.Equal([first, second], applied.Outputs);
        Assert.Equal("Applied Grouped in Span mode to 2 outputs.", viewModel.StatusMessage);
    }

    [Fact]
    public async Task ApplyWallpaperAsync_WhenUnexpectedCommandFailure_ShowsStableGenericError()
    {
        var commands = new RecordingCommands { Failure = new IOException("injected") };
        var (viewModel, output) = WallpaperViewModel(commands);

        await viewModel.ApplyWallpaperAsync([output], TestContext.Current.CancellationToken);

        Assert.Equal("wallpaper.apply.failed", viewModel.ErrorCode);
        Assert.Equal("Không thể đặt wallpaper.", viewModel.StatusMessage);
        Assert.False(viewModel.IsBusy);
    }

    [Fact]
    public async Task ApplyWallpaperAsync_WhenCallerCancels_IsSilentAndRestoresBusy()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var commands = new RecordingCommands { ObserveCancellation = true };
        var (viewModel, output) = WallpaperViewModel(commands);

        await viewModel.ApplyWallpaperAsync([output], cancellation.Token);

        Assert.Null(viewModel.ErrorCode);
        Assert.False(viewModel.IsBusy);
        Assert.Empty(commands.WallpapersApplied);
    }

    [Fact]
    public async Task ApplyWallpaperAsync_RepeatedCallWhileBusy_IssuesOnlyOneCommand()
    {
        var commands = new RecordingCommands { WallpaperBarrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously) };
        var (viewModel, output) = WallpaperViewModel(commands);

        var first = viewModel.ApplyWallpaperAsync([output], TestContext.Current.CancellationToken);
        await commands.WallpaperStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await viewModel.ApplyWallpaperAsync([output], TestContext.Current.CancellationToken);
        commands.WallpaperBarrier.SetResult();
        await first;

        Assert.Equal(1, commands.WallpaperCalls);
        Assert.False(viewModel.IsBusy);
    }

    private static (ManagementWindowViewModel ViewModel, MonitorIdentity Output) WallpaperViewModel(
        RecordingCommands commands)
    {
        var definition = new WallpaperDefinition(
            WallpaperId.New(), "Aurora", SolidColorSource.Create("#234567"),
            FitMode.Cover, 30, false, false, 0, false);
        var item = new WallpaperLibraryItem(
            definition, null, null,
            new SourceValidation(SourceValidationStatus.Available, null, null, DateTimeOffset.UnixEpoch));
        var output = new MonitorIdentity("DISPLAY-A");
        var viewModel = new ManagementWindowViewModel(commands);
        viewModel.Load(new EngineSnapshot(
            new PersistedState(1, [item], [], [], null),
            [Output(output)]));
        return (viewModel, output);
    }

    private static OutputRuntimeSnapshot Output(MonitorIdentity output) =>
        new(output, 1, null, null, new EffectiveWallpaperState(null, EffectiveWallpaperKind.SolidFallback, null, null));

    private sealed class RecordingTrayBackend(bool succeeds) : ITrayIconBackend
    {
        public TrayIconMenu? Menu { get; private set; }
        public bool LastPaused { get; private set; }
        public event Action<bool, string?>? AvailabilityChanged;

        public bool TryCreate(TrayIconMenu menu)
        {
            Menu = menu;
            return succeeds;
        }

        public void SetPaused(bool paused)
        {
            LastPaused = paused;
        }

        public void Dispose()
        {
        }

        public void PublishAvailability(bool available, string? error) =>
            AvailabilityChanged?.Invoke(available, error);
    }

    private sealed class ThrowingTrayBackend : ITrayIconBackend
    {
        public event Action<bool, string?>? AvailabilityChanged
        {
            add { }
            remove { }
        }

        public bool TryCreate(TrayIconMenu menu) =>
            throw new InvalidOperationException("Shell_NotifyIcon(NIM_ADD) failed.");

        public void SetPaused(bool paused)
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class RecordingManagementWindow : IManagementWindow
    {
        public bool IsVisible { get; set; } = true;
        public bool IsMinimized { get; set; }
        public int HideCount { get; private set; }
        public int BringToFrontCount { get; private set; }

        public void Show() => IsVisible = true;
        public void Hide()
        {
            IsVisible = false;
            HideCount++;
        }

        public void Restore() => IsMinimized = false;
        public void BringToFront() => BringToFrontCount++;
    }

    private sealed class RecordingCommands : IManagementWallpaperCommands
    {
        public List<(DisplayMode Mode, IReadOnlyList<MonitorIdentity> Outputs, string Color)> Applied { get; } = [];
        public List<(WallpaperId Wallpaper, DisplayMode Mode, IReadOnlyList<MonitorIdentity> Outputs)> WallpapersApplied { get; } = [];
        public Exception? Failure { get; init; }
        public bool ObserveCancellation { get; init; }
        public TaskCompletionSource? WallpaperBarrier { get; init; }
        public TaskCompletionSource WallpaperStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WallpaperCalls { get; private set; }

        public Task ApplyColorAsync(
            string color,
            DisplayMode mode,
            IReadOnlyList<MonitorIdentity> outputs,
            CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            Applied.Add((mode, outputs.ToArray(), color));
            return Task.CompletedTask;
        }

        public Task ApplyWallpaperAsync(
            WallpaperId wallpaper,
            DisplayMode mode,
            IReadOnlyList<MonitorIdentity> outputs,
            CancellationToken cancellationToken)
        {
            WallpaperCalls++;
            WallpaperStarted.TrySetResult();
            if (ObserveCancellation) cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null)
            {
                return Task.FromException(Failure);
            }

            WallpapersApplied.Add((wallpaper, mode, outputs.ToArray()));
            return WallpaperBarrier?.Task ?? Task.CompletedTask;
        }
    }
}
