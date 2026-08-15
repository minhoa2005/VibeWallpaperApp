#nullable enable
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VibeWallpaper.App.Coordination;
using VibeWallpaper.App.Services;
using VibeWallpaper.App.ViewModels;
using VibeWallpaper.Engine.Core.Activity;
using VibeWallpaper.Engine.Activity;
using VibeWallpaper.Engine.Core.Monitors;
using VibeWallpaper.Engine.Core.Persistence;
using VibeWallpaper.Engine.Core.Rendering;
using VibeWallpaper.Engine.Core.Wallpapers;
using VibeWallpaper.Engine.Desktop;
using VibeWallpaper.Engine.Diagnostics;
using VibeWallpaper.Engine.Import;
using VibeWallpaper.Engine.Import.Video;
using VibeWallpaper.Engine.Monitors;
using VibeWallpaper.Engine.Persistence;
using VibeWallpaper.Engine.Rendering.Solid;
using VibeWallpaper.Engine.Rendering.Video.Diagnostics;
using VibeWallpaper.Engine.Runtime;
using VibeWallpaper.Engine.Sources;

namespace VibeWallpaper.App;

public partial class App : Application, IManagementWallpaperCommands
{
    private readonly Dictionary<string, IWallpaperHostWindow> _hosts = new(StringComparer.Ordinal);
    private readonly List<FallbackDiagnostic> _startupDiagnostics = [];
    private readonly StartupFailureReporter _startupFailureReporter = new(
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeWallpaper",
            "Logs"));
    private readonly StartupFailureNotifier _startupFailureNotifier = new();
    private ApplicationCoordinator? _coordinator;
    private SingleInstanceService? _singleInstance;
    private RollingFileLogSink? _log;
    private EngineStaDispatcher? _dispatcher;
    private DesktopHostProvider? _hostProvider;
    private WallpaperAssignmentCoordinator? _runtime;
    private WallpaperEngine? _engine;
    private FallbackRendererCoordinator? _fallback;
    private ApplicationRendererServices? _rendererServices;
    private DisplayTopologySnapshot? _topology;
    private AppSettings _settings = AppSettings.Default;
    private PersistedState _state = PersistedState.Default;
    private StateStore? _stateStore;
    private SourceChangeMonitor? _sourceChanges;
    private TrayIconService? _tray;
    private MainWindow? _window;
    private ManagementWindowController? _windowController;

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args) => _ = LaunchAsync();

    public async Task ApplyColorAsync(
        string color,
        DisplayMode mode,
        IReadOnlyList<MonitorIdentity> outputs,
        CancellationToken cancellationToken)
    {
        if (_engine is null || _topology is null)
            throw new WallpaperCommandException("wallpaper.engine.unavailable", "Wallpaper engine unavailable.");
        var definition = new WallpaperDefinition(WallpaperId.New(), $"Solid {color}", SolidColorSource.Create(color), FitMode.Cover, 30, false, false, 0, false);
        try
        {
            var request = WallpaperAssignmentRequestPlanner.Plan(
                definition,
                mode,
                outputs,
                _topology,
                _hosts.ToDictionary(static pair => pair.Key, static pair => pair.Value.Hwnd, StringComparer.Ordinal));
            var result = await _engine.ApplyAsync(request, cancellationToken);
            if (result.Outcome != AssignmentOutcome.Applied)
                throw new WallpaperCommandException("wallpaper.assignment.superseded", "A newer wallpaper change replaced this command.");
            RefreshWindow();
        }
        catch (WallpaperCommandException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new WallpaperCommandException("wallpaper.apply.failed", "The color could not be applied.", exception);
        }
    }

    public async Task ApplyWallpaperAsync(
        WallpaperId wallpaper,
        DisplayMode mode,
        IReadOnlyList<MonitorIdentity> outputs,
        CancellationToken cancellationToken)
    {
        if (_engine is null || _topology is null)
            throw new WallpaperCommandException("wallpaper.engine.unavailable", "Wallpaper engine unavailable.");

        var definition = RuntimeWallpaperResolver.Find(_engine.GetSnapshot(), wallpaper)
            ?? throw new WallpaperCommandException("wallpaper.library.missing", "The selected wallpaper is no longer in the library.");
        try
        {
            var request = WallpaperAssignmentRequestPlanner.Plan(
                definition,
                mode,
                outputs,
                _topology,
                _hosts.ToDictionary(static pair => pair.Key, static pair => pair.Value.Hwnd, StringComparer.Ordinal));
            var result = await _engine.ApplyAsync(request, cancellationToken);
            if (result.Outcome != AssignmentOutcome.Applied)
                throw new WallpaperCommandException("wallpaper.assignment.superseded", "A newer wallpaper change replaced this command.");
            RefreshWindow();
        }
        catch (WallpaperCommandException) { throw; }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            throw new WallpaperCommandException("wallpaper.apply.failed", "The wallpaper could not be applied.", exception);
        }
    }

    private async Task LaunchAsync()
    {
        try
        {
            var dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            var stages = new IApplicationStage[]
            {
                new DelegateStage(ApplicationStageKind.PerMonitorV2, _ => { ConfigureDpi(); return Task.CompletedTask; }),
                new DelegateStage(ApplicationStageKind.SingleInstance, async token =>
                {
                    _singleInstance = new SingleInstanceService("VibeWallpaper", new DispatcherActivation(dispatcherQueue));
                    var result = await _singleInstance.StartAsync(ActivateManagementWindowAsync, token);
                    if (result == SingleInstanceStartResult.SecondaryActivationSent) throw new SecondaryInstanceException();
                }, async () => { if (_singleInstance is not null) await _singleInstance.DisposeAsync(); }),
                new DelegateStage(ApplicationStageKind.LoggingConfigurationState, LoadStateAsync, async () => { if (_log is not null) await _log.DisposeAsync(); }),
                new DelegateStage(ApplicationStageKind.EngineDispatcher, async _ => _dispatcher = await EngineStaDispatcher.StartAsync(), async () => { if (_dispatcher is not null) await _dispatcher.DisposeAsync(); }),
                new DelegateStage(ApplicationStageKind.TopologyAndDesktopHosts, CreateHostsAsync, async () => { if (_hostProvider is not null) await _hostProvider.DisposeAsync(); _hosts.Clear(); }),
                new DelegateStage(ApplicationStageKind.RestoreAssignments, RestoreAssignmentsAsync, async () => { if (_engine is not null) await _engine.DisposeAsync(); }),
                new ActivityObserversStage(CreateActivityObservationServices, CreateSourceMonitoringServices),
                new DelegateStage(ApplicationStageKind.TrayAndUi, CreateUiAsync, DisposeUiAsync),
            };
            _coordinator = new ApplicationCoordinator(
                stages,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(3));
            await _coordinator.StartAsync(CancellationToken.None);
        }
        catch (SecondaryInstanceException)
        {
            Current.Exit();
        }
        catch (Exception exception)
        {
            try
            {
                await _startupFailureReporter.ReportAsync(exception);
            }
            finally
            {
                _startupFailureNotifier.Show();
                Current.Exit();
            }
        }
    }

    private async Task LoadStateAsync(CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VibeWallpaper");
        _log = new RollingFileLogSink(Path.Combine(directory, "Logs"));
        var settingsStore = new SettingsStore(directory);
        _stateStore = new StateStore(directory);
        var settingsTask = settingsStore.LoadAsync(cancellationToken);
        var stateTask = _stateStore.LoadAsync(cancellationToken);
        await Task.WhenAll(settingsTask, stateTask);
        _settings = settingsTask.Result.Value;
        _state = stateTask.Result.Value;
        await _log.WriteAsync("info", "Configuration and state loaded.", cancellationToken: cancellationToken);
    }

    private async Task CreateHostsAsync(CancellationToken cancellationToken)
    {
        _topology = new DisplayConfigTopologyService().Capture();
        _hostProvider = new DesktopHostProvider(_dispatcher!);
        foreach (var output in _topology.LogicalOutputs)
            _hosts[output.Descriptor.Identity.Key] = await _hostProvider.CreateAsync(output.Descriptor, cancellationToken);
    }

    private async Task RestoreAssignmentsAsync(CancellationToken cancellationToken)
    {
        var webViewData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "VibeWallpaper",
            "WebView2");
        _rendererServices = ApplicationRendererServices.CreateDefault(_dispatcher!, webViewData, _log);
        var playbackDiagnostics = _log is null
            ? LogSinkVideoPlaybackDiagnostics.None
            : new LogSinkVideoPlaybackDiagnostics(_log);
        _runtime = new WallpaperAssignmentCoordinator(
            _dispatcher!,
            _rendererServices.Factory,
            _stateStore!,
            _state,
            diagnostics: playbackDiagnostics);
        _fallback = new FallbackRendererCoordinator(
            _state,
            _settings,
            new AppRuntimeActivator(this));
        _engine = new WallpaperEngine(_runtime, _fallback, [_rendererServices]);
        var result = await _engine.InitializeFallbacksAsync(
            ApplicationRendererServices.SupportsRenderer,
            cancellationToken);
        _startupDiagnostics.AddRange(result.Diagnostics);
        foreach (var diagnostic in result.Diagnostics)
        {
            if (_log is not null)
            {
                await _log.WriteAsync(
                    "error",
                    $"{diagnostic.Code}: {diagnostic.Message}",
                    diagnostic.Exception,
                    cancellationToken: cancellationToken);
            }
        }
    }

    private SourceMonitoringServices CreateSourceMonitoringServices()
    {
        var library = new WallpaperLibraryService(_stateStore!, new VideoProbeService());
        var changes = new SourceChangeMonitor(_stateStore!);
        _sourceChanges = changes;
        var revalidator = new VideoSourceRevalidator(
            _stateStore!, library, _fallback!, ApplicationRendererServices.SupportsRenderer);
        var active = new ActiveVideoSourceMonitor(
            _stateStore!,
            changes,
            revalidator,
            interval: TimeSpan.FromSeconds(30),
            perSourceTimeout: TimeSpan.FromSeconds(8));
        return new SourceMonitoringServices(changes, active);
    }

    private ActivityObservationServices CreateActivityObservationServices()
    {
        var topology = new DisplayConfigTopologyService();
        var facts = new WindowsActivitySystemFactsProvider();
        var context = new WindowsActivityWindowContextProvider(
            () => _hosts.Values.Select(static host => host.Hwnd).ToArray());
        var builder = new ActivitySnapshotBuilder(
            topology,
            new WindowSnapshotProvider(),
            facts,
            context);
        var monitor = new ActivityMonitor(builder, evidenceConsumer: facts);
        var observers = new IActivityObserver[]
        {
            new WindowEventObserver(monitor),
            new PowerSessionObserver(monitor, facts),
            new RemoteDesktopObserver(monitor),
        };
        return new ActivityObservationServices(monitor, observers, async (snapshot, cancellationToken) =>
        {
            if (_engine is null) return;
            try
            {
                var activeOutputs = topology.Capture().LogicalOutputs
                    .Select(static output => output.Descriptor.Identity)
                    .Where(output => _hosts.ContainsKey(output.Key))
                    .ToArray();
                await _engine.ApplyActivitySnapshotAsync(
                    activeOutputs,
                    snapshot,
                    PolicyOptions(_settings),
                    null,
                    new HashSet<PerformanceReason>(),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                if (_log is not null)
                    await _log.WriteAsync("error", "Activity policy application failed.", exception);
            }
        });
    }

    private static PerformancePolicyOptions PolicyOptions(AppSettings settings) => new(
        settings.SuspendOnFullscreen,
        settings.SuspendOnMaximized,
        settings.SuspendOnRemoteDesktop,
        settings.SuspendOnSessionLock,
        settings.SuspendOnDisplayOff,
        settings.SuspendOnSystemSleep,
        settings.BatteryTargetFps,
        settings.BatterySaverTargetFps,
        settings.IncompatibleThrottle);

    private async Task CreateUiAsync(CancellationToken cancellationToken)
    {
        _tray = new TrayIconService();
        _window = new MainWindow(new ManagementWindowViewModel(this));
        var libraryController = new LibraryController(
            new WallpaperImportPreparer(new VideoProbeService()),
            _engine!,
            _log,
            _ => QueueLibraryRefresh());
        var libraryViewModel = new LibraryViewModel(
            libraryController,
            new ContentPicker(_window.Hwnd),
            new LibraryDialogService(() => _window?.DialogXamlRoot),
            await libraryController.GetLibraryAsync(cancellationToken));
        _window.AttachLibrary(libraryViewModel);
        _windowController = new ManagementWindowController(_window, _tray);
        _window.AttachController(_windowController);
        _window.ExitRequested += () => _ = ShutdownAsync();
        _tray.PauseResumeRequested += async () => await TogglePauseAsync();
        _tray.ExitRequested += () => _ = ShutdownAsync();
        if (!_tray.TryStart())
        {
            var trayError = $"Tray icon startup failed: {_tray.LastError ?? "Unknown tray error."}";
            try
            {
                if (_log is not null)
                {
                    await _log.WriteAsync("error", trayError, cancellationToken: cancellationToken);
                }
            }
            catch (Exception exception)
            {
                await _startupFailureReporter.ReportAsync(
                    new InvalidOperationException(trayError, exception),
                    cancellationToken);
            }
        }
        RefreshWindow();
        if (_startupDiagnostics.FirstOrDefault() is { } diagnostic)
        {
            _window.ShowCommandResult(diagnostic.Message, diagnostic.Code);
        }
        _window.Show();
    }

    private ValueTask DisposeUiAsync()
    {
        _windowController?.PermitClose();
        _tray?.Dispose();
        _window?.Close();
        _window = null;
        _windowController = null;
        return ValueTask.CompletedTask;
    }

    private async Task TogglePauseAsync()
    {
        if (_engine is null || _topology is null) return;
        var requested = !_engine.IsPaused;
        var result = await _engine.SetPausedAllAsync(
            _topology.LogicalOutputs.Select(static output => output.Descriptor.Identity).ToArray(),
            requested,
            CancellationToken.None);
        if (result.Success)
        {
            _tray?.SetPaused(_engine.IsPaused);
            RefreshWindow();
        }

        _window?.ShowCommandResult(result.Message ?? "Pause command completed.", result.ErrorCode);
    }

    private Task ActivateManagementWindowAsync()
    {
        if (_window is not null)
        {
            if (_window.IsMinimized) _window.Restore();
            _window.Show();
            _window.BringToFront();
        }
        return Task.CompletedTask;
    }

    private async Task ShutdownAsync()
    {
        if (_coordinator is not null) await _coordinator.StopAsync();
        Current.Exit();
    }

    private void RefreshWindow()
    {
        if (_window is null || _topology is null) return;
        var runtime = _engine?.GetSnapshot();
        var outputs = _topology.LogicalOutputs.Select(output =>
            runtime?.Outputs.FirstOrDefault(item => item.Output == output.Descriptor.Identity) ??
            new OutputRuntimeSnapshot(output.Descriptor.Identity, 0, null, null)).ToArray();
        _window.LoadSnapshot(new EngineSnapshot(runtime?.State ?? _state, outputs));
    }

    private void QueueLibraryRefresh()
    {
        var window = _window;
        if (window is null) return;
        _ = window.DispatcherQueue.TryEnqueue(() =>
        {
            RefreshWindow();
        });
        _ = RefreshSourceMonitoringAsync();
    }

    private async Task RefreshSourceMonitoringAsync()
    {
        if (_sourceChanges is null) return;
        try
        {
            await _sourceChanges.RefreshAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            if (_log is not null)
                await _log.WriteAsync("error", "Source monitoring refresh failed after a library change.", exception);
        }
    }

    private static void ConfigureDpi()
    {
        if (!SetProcessDpiAwarenessContext(new nint(-4)) && Marshal.GetLastPInvokeError() != 5)
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetProcessDpiAwarenessContext(PMv2) failed.");
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);

    private sealed class AppRuntimeActivator(App app) : IRuntimeWallpaperActivator
    {
        public async Task ActivateAsync(
            MonitorIdentity output,
            WallpaperDefinition wallpaper,
            WallpaperAssignment persistedAssignment,
            long generation,
            CancellationToken cancellationToken)
        {
            var topology = app._topology
                ?? throw new InvalidOperationException("Display topology is unavailable during wallpaper restore.");
            var runtime = app._runtime
                ?? throw new InvalidOperationException("Wallpaper runtime is unavailable during wallpaper restore.");
            var state = app._engine?.GetSnapshot().State ?? app._state;
            IReadOnlyList<MonitorIdentity> selectedOutputs;
            if (persistedAssignment.Mode == DisplayMode.Independent)
            {
                selectedOutputs = [output];
            }
            else
            {
                var groupId = persistedAssignment.GroupId
                    ?? throw new InvalidOperationException("A grouped assignment is missing its display-group ID.");
                selectedOutputs = state.Groups.FirstOrDefault(group => group.Id == groupId)?.Members
                    ?? state.Assignments
                        .Where(assignment => assignment.GroupId == groupId)
                        .Select(assignment => assignment.Monitor.Identity)
                        .ToArray();
            }

            var settings = state.Assignments
                .Where(assignment => selectedOutputs.Contains(assignment.Monitor.Identity))
                .ToDictionary(
                    assignment => assignment.Monitor.Identity.Key,
                    assignment => new OutputWallpaperSettings(
                        assignment.Fit,
                        assignment.TargetFps,
                        assignment.VolumePercent),
                    StringComparer.Ordinal);
            settings.TryAdd(
                output.Key,
                new OutputWallpaperSettings(
                    persistedAssignment.Fit,
                    persistedAssignment.TargetFps,
                    persistedAssignment.VolumePercent));

            var connectedOutputKeys = topology.LogicalOutputs
                .Select(static item => item.Descriptor.Identity.Key)
                .ToHashSet(StringComparer.Ordinal);
            if (selectedOutputs.Any(selected => !connectedOutputKeys.Contains(selected.Key)))
            {
                throw new ArgumentException(
                    "One or more persisted outputs are no longer connected.",
                    "selectedOutputs");
            }

            if (selectedOutputs.Any(selected => !app._hosts.ContainsKey(selected.Key)))
            {
                throw new InvalidOperationException("A desktop host is unavailable for a persisted output.");
            }

            var request = WallpaperAssignmentRequestPlanner.Plan(
                wallpaper,
                persistedAssignment.Mode,
                selectedOutputs,
                topology,
                app._hosts.ToDictionary(static pair => pair.Key, static pair => pair.Value.Hwnd, StringComparer.Ordinal),
                persistedAssignment.GroupId,
                settings);
            var result = await runtime.ApplyRuntimeOnlyAsync(request, cancellationToken);
            FallbackRuntimeActivationGuard.EnsureApplied(result, output);
        }
    }

    private sealed class DispatcherActivation(DispatcherQueue dispatcher) : IActivationDispatcher
    {
        public Task DispatchAsync(Func<Task> callback)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(async () =>
            {
                try { await callback(); completion.TrySetResult(); }
                catch (Exception exception) { completion.TrySetException(exception); }
            })) completion.TrySetException(new InvalidOperationException("The WinUI dispatcher is unavailable."));
            return completion.Task;
        }
    }

    private sealed class DelegateStage(
        ApplicationStageKind kind,
        Func<CancellationToken, Task> start,
        Func<ValueTask>? stop = null) : IApplicationStage
    {
        public ApplicationStageKind Kind => kind;
        public Task StartAsync(CancellationToken cancellationToken) => start(cancellationToken);
        public ValueTask DisposeAsync() => stop?.Invoke() ?? ValueTask.CompletedTask;
    }

    private sealed class SecondaryInstanceException : Exception;
}
