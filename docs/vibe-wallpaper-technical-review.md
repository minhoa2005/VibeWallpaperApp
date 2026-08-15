# Vibe Wallpaper — Technical Design Review and Implementation Recommendations

**Date:** 2026-07-31  
**Target:** Windows 11 x64  
**Application type:** Local desktop animated-wallpaper application  
**Primary technologies:** C#, .NET, WinUI 3, Win32, DWM, COM, WebView2, LibVLCSharp

---

## 1. Overall Assessment

The proposed design is technically sound and can be implemented on Windows 11.

The strongest decisions are:

- separating the WinUI 3 management interface from the wallpaper output windows;
- using one native wallpaper host window per physical monitor;
- isolating undocumented `WorkerW` integration inside a dedicated component;
- centralizing renderer ownership and lifecycle management in `WallpaperEngine`;
- using reason-based performance policies instead of a single pause flag;
- treating local web wallpapers as untrusted content;
- preserving monitor assignments across Explorer restarts and display-topology changes;
- defining explicit failure handling, recovery, logging, and test requirements.

The design should be retained, but several implementation details need to be clarified before coding:

1. renderer lifecycle state and performance state must be separated;
2. threading and operation serialization must be explicitly defined;
3. renderer replacement should follow a prepare–swap–commit flow;
4. one-process architecture cannot guarantee complete renderer crash isolation;
5. WebView2 interaction should use a composition controller and interaction overlays;
6. fullscreen detection must account for foreground state and window z-order;
7. web throttling and span behavior should be documented as renderer-dependent or best effort;
8. monitor identity must not rely only on `HMONITOR` or display indexes;
9. startup, shutdown, Explorer restart, and monitor hot-plug flows should be deterministic;
10. imported web folders should be revalidated when their contents change.

---

## 2. Technology Review

## 2.1 Language and Runtime

C# and a supported .NET LTS version are appropriate for the project.

Benefits include:

- good Win32 and COM interoperability;
- strong asynchronous programming support;
- straightforward JSON serialization;
- established testing libraries;
- convenient packaging for a self-contained x64 application.

The project should pin its .NET SDK through `global.json` so development and build machines use the same toolchain.

Example:

```json
{
  "sdk": {
    "version": "10.0.100",
    "rollForward": "latestPatch"
  }
}
```

Patch versions may be updated after compatibility testing.

---

## 2.2 WinUI 3 and Windows App SDK

WinUI 3 is suitable for the management window.

It should be used for:

- navigation;
- monitor and wallpaper management;
- settings;
- dialogs and notifications;
- accessibility;
- theme support;
- drag-and-drop;
- system-tray commands exposed through the application shell.

WinUI 3 should not be used as the rendering surface for every monitor. Native Win32 host windows remain the better choice for wallpaper output because they are lightweight and easier to attach to the Desktop window hierarchy.

The Windows App SDK package should be pinned to a stable version at the beginning of implementation. Avoid floating package versions.

Example:

```xml
<Project>
  <ItemGroup>
    <PackageVersion Include="Microsoft.WindowsAppSDK"
                    Version="PINNED_STABLE_VERSION" />
  </ItemGroup>
</Project>
```

---

## 2.3 Win32, DWM, and COM Integration

Win32, DWM, and COM are required for:

- discovering the Desktop host window;
- attaching wallpaper windows behind Desktop icons;
- managing monitor-native windows;
- detecting window coverage;
- reading DWM extended-frame bounds;
- handling hotkeys and system messages;
- monitoring Explorer lifecycle;
- receiving session and display notifications.

All native interop should be wrapped in focused services.

Recommended structure:

```text
Native/
├── User32Native.cs
├── DwmApiNative.cs
├── Shell32Native.cs
├── Kernel32Native.cs
├── DisplayConfigNative.cs
└── SafeHandles/
```

Avoid scattering raw P/Invoke declarations across view models, renderers, or policy code.

Use:

- `SafeHandle` where ownership is clear;
- explicit ownership comments for raw HWND values;
- native error conversion through `Marshal.GetLastWin32Error()`;
- narrow wrapper methods rather than exposing raw native functions everywhere.

---

## 2.4 WebView2

WebView2 Evergreen is appropriate for local HTML and WebGL wallpapers.

Recommended design:

- one shared `CoreWebView2Environment`;
- one user-data directory owned by the application;
- one controller per active web renderer;
- one virtual hostname per wallpaper;
- no native host objects exposed to JavaScript;
- external network access denied by default;
- explicit permission and navigation handlers;
- process-failure monitoring;
- controlled suspend and resume behavior.

For wallpaper rendering and synthetic input, use:

```text
CoreWebView2CompositionController
```

instead of depending only on the standard HWND-based controller.

The composition controller is better suited to:

- sending pointer input programmatically;
- rendering into a native composition surface;
- operating when normal Desktop hit testing is disabled;
- implementing passive and interactive wallpaper input consistently.

The application must check whether the WebView2 runtime is available and provide a clear error or installation instruction when it is missing.

A .NET self-contained build does not automatically make the WebView2 runtime self-contained.

---

## 2.5 LibVLCSharp

LibVLCSharp is a suitable video backend because it supports:

- a wide range of codecs and containers;
- hardware decoding when available;
- native HWND rendering;
- pause, seek, mute, and looping;
- mature Windows support.

The renderer should use LibVLC through a native child HWND rather than embedding a WinUI-specific video control inside each wallpaper host.

Keep the following ownership rule:

```text
One VideoRenderer
    owns one MediaPlayer
    owns or references one Media
    renders into one monitor host
```

Do not share one `MediaPlayer` instance between monitor hosts.

For duplicate and span modes, multiple renderers may share a logical playback clock but should retain separate native players unless shared decoding is deliberately implemented later.

---

## 2.6 Configuration and Persistence

`System.Text.Json` is sufficient.

Recommended documents:

```text
settings.json
library.json
assignments.json
settings.backup.json
```

Each document should contain:

```csharp
public int SchemaVersion { get; init; }
```

Writes should use atomic replacement:

```text
Serialize to temporary file
        ↓
Flush temporary file
        ↓
Validate serialized output
        ↓
Replace current file
        ↓
Retain one known-good backup
```

Never write directly over the only valid copy.

Invalid files should be preserved for diagnosis instead of silently deleted.

---

## 2.7 Testing Stack

xUnit is suitable for:

- policy evaluation;
- state transitions;
- monitor reconciliation;
- coordinate conversion;
- configuration migration;
- renderer orchestration;
- import validation.

Native, WebView2, and LibVLC behavior also require integration and manual Windows testing because several behaviors cannot be reliably validated in pure unit tests.

---

## 3. Recommended Application Architecture

```text
VibeWallpaper.exe
│
├── AppShell
├── ApplicationCoordinator
├── WallpaperEngine
├── DesktopHost
├── MonitorManager
├── ActivityMonitor
├── InteractionManager
├── RendererFactory
│   ├── VideoRenderer
│   └── WebRenderer
├── SettingsStore
├── LibraryStore
├── Diagnostics
└── Native Interop
```

---

## 3.1 AppShell

Responsibilities:

- own the WinUI 3 management window;
- navigation and view models;
- dialogs and InfoBars;
- notification-area commands;
- startup setting;
- hotkey configuration UI;
- opening and hiding the management window.

AppShell must not:

- search for WorkerW;
- create renderer instances directly;
- call LibVLC or WebView2 lifecycle APIs directly;
- own monitor wallpaper state;
- evaluate fullscreen policy.

All engine actions should go through application interfaces.

---

## 3.2 ApplicationCoordinator

A coordinator should own application startup and shutdown ordering.

Responsibilities:

- load configuration;
- initialize the engine thread;
- initialize Desktop hosting;
- restore monitor assignments;
- start system monitoring;
- register tray and hotkey behavior;
- orchestrate controlled exit.

This avoids placing application lifecycle logic inside `App.xaml.cs` or the main view model.

---

## 3.3 DesktopHost

Responsibilities:

- discover `Progman`, `WorkerW`, and related Desktop windows;
- validate whether cached handles are still alive;
- create or attach one wallpaper host window per monitor;
- detach and destroy wallpaper host windows during shutdown;
- recover after Explorer restarts;
- hide active wallpaper hosts while the Desktop hierarchy is unavailable.

The undocumented Desktop integration must remain isolated behind an interface.

Example:

```csharp
public interface IDesktopHost
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<DesktopAttachment> AttachAsync(
        WallpaperHostWindow window,
        MonitorIdentity monitor,
        CancellationToken cancellationToken);

    Task DetachAsync(
        DesktopAttachment attachment,
        CancellationToken cancellationToken);

    Task ReattachAllAsync(CancellationToken cancellationToken);
}
```

No component outside DesktopHost should retain WorkerW-specific HWND values.

---

## 3.4 MonitorManager

Responsibilities:

- enumerate active displays;
- expose monitor bounds in virtual-screen coordinates;
- expose work area, DPI, orientation, and primary state;
- detect topology changes;
- reconcile active displays with persisted assignments;
- retain assignments for temporarily disconnected displays.

A monitor must not be identified only by:

- `HMONITOR`;
- display index;
- `\\.\DISPLAY1`;
- current virtual-screen position.

Recommended identity inputs:

```text
DisplayConfig adapter LUID
DisplayConfig target ID
monitor device path
EDID manufacturer/product/serial when available
connector information
previous bounds and orientation as fallback hints
```

Monitor matching should use a hierarchy:

```text
Exact device path
    ↓
EDID-derived identity
    ↓
Adapter/target identity
    ↓
Previous topology similarity
    ↓
Treat as a new monitor
```

The implementation must tolerate identical monitor models with missing or duplicated serial values.

---

## 3.5 WallpaperEngine

WallpaperEngine is the authoritative owner of:

- monitor assignments;
- renderer instances;
- renderer replacement;
- display modes;
- effective performance states;
- renderer fault recovery;
- renderer disposal.

Only WallpaperEngine may:

- create a renderer;
- attach a renderer to a monitor;
- replace a renderer;
- suspend or resume a renderer;
- dispose a renderer;
- commit an active assignment.

This prevents race conditions caused by UI, monitor events, and power events acting independently.

---

## 3.6 ActivityMonitor

Responsibilities:

- foreground-window changes;
- relevant window-location changes;
- fullscreen and maximized-window evaluation;
- session lock and unlock;
- sleep and resume;
- display on and off;
- battery and battery-saver state;
- Remote Desktop state;
- Explorer lifecycle;
- fallback state reconciliation.

ActivityMonitor should emit facts or reasons. It should not directly call renderers.

Example:

```csharp
public sealed record ActivitySnapshot(
    bool SessionLocked,
    bool DisplayOff,
    bool SystemSleeping,
    bool RunningOnBattery,
    bool BatterySaverEnabled,
    bool RemoteDesktopSession,
    IReadOnlySet<MonitorIdentity> FullscreenCoveredMonitors);
```

WallpaperEngine converts this snapshot into per-monitor performance policies.

---

## 3.7 InteractionManager

Responsibilities:

- passive global pointer sampling;
- pointer-coordinate conversion;
- active interaction-mode lifecycle;
- interaction overlays;
- synthetic WebView2 input;
- interaction timeout;
- safe exit on `Esc`, session lock, or context loss.

InteractionManager must never expose arbitrary native application services to wallpaper JavaScript.

---

## 3.8 Diagnostics

Diagnostics should record:

- component name;
- monitor identity;
- renderer identity;
- operation name;
- elapsed time;
- failure category;
- native or managed error code;
- retry count;
- state transition.

It must not record:

- wallpaper file contents;
- JavaScript source;
- typed keystrokes;
- clipboard content;
- private browser data.

Use bounded rolling files and cap disk usage.

---

## 4. Threading Model

The threading model must be explicit.

Recommended arrangement:

## 4.1 WinUI Thread

Owns:

- WinUI controls;
- navigation;
- dialogs;
- view models;
- management-window state.

UI commands should call asynchronous engine interfaces and await results without blocking the UI thread.

---

## 4.2 Engine STA Thread

Owns:

- DesktopHost operations;
- wallpaper host window creation;
- native message dispatch;
- WebView2 environments and controllers;
- serialized renderer lifecycle operations;
- monitor reconciliation;
- operation ordering.

The engine thread should run a message loop.

All WebView2 controller lifecycle calls should occur on the owning thread.

---

## 4.3 Background Worker Tasks

Use background tasks for:

- video probing;
- thumbnail extraction;
- file hashing;
- directory fingerprinting;
- non-UI JSON preparation;
- log compression or cleanup;
- media metadata extraction.

Background code must return results to the engine rather than mutating active renderer state directly.

---

## 4.4 Per-Monitor Operation Serialization

Each monitor runtime should serialize transitions.

Example:

```csharp
public sealed class MonitorRuntime
{
    public required MonitorIdentity Identity { get; init; }

    public SemaphoreSlim TransitionLock { get; } = new(1, 1);

    public long AssignmentGeneration { get; set; }

    public IWallpaperRenderer? Renderer { get; set; }

    public HashSet<PerformanceReason> Reasons { get; } = [];

    public PerformanceState EffectiveState { get; set; }
}
```

Every renderer-changing operation should:

1. acquire the monitor transition lock;
2. verify the current assignment generation;
3. honor cancellation;
4. make state changes idempotently;
5. release the lock in `finally`.

This prevents overlapping operations such as:

```text
Change wallpaper
Monitor disconnect
Fullscreen event
Explorer restart
Application exit
```

from corrupting state.

---

## 5. Renderer State Model

Do not combine lifecycle state and performance state into one state machine.

---

## 5.1 Lifecycle State

Recommended lifecycle:

```text
Created
    ↓
Initializing
    ↓
Loading
    ↓
Ready
    ↓
Active
    ↓
Stopped
    ↓
Disposed
```

Any non-disposed state may transition to:

```text
Faulted
```

Suggested enum:

```csharp
public enum RendererLifecycle
{
    Created,
    Initializing,
    Loading,
    Ready,
    Active,
    Stopped,
    Faulted,
    Disposed
}
```

---

## 5.2 Performance State

Performance state is separate:

```csharp
public enum PerformanceState
{
    Running,
    Throttled,
    Suspended
}
```

Precedence:

```text
Suspended > Throttled > Running
```

Performance state only applies when the renderer is sufficiently initialized.

A renderer may be:

```text
Lifecycle = Active
Performance = Suspended
```

This is clearer than treating `Suspend` as a lifecycle stage.

---

## 5.3 Renderer Contract

Recommended interface:

```csharp
public interface IWallpaperRenderer : IAsyncDisposable
{
    RendererLifecycle Lifecycle { get; }

    PerformanceState PerformanceState { get; }

    Task InitializeAsync(
        RendererContext context,
        CancellationToken cancellationToken);

    Task LoadAsync(
        WallpaperSource source,
        CancellationToken cancellationToken);

    Task ActivateAsync(CancellationToken cancellationToken);

    Task SetPerformanceStateAsync(
        PerformanceState state,
        CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
```

Requirements:

- repeated state requests should be safe;
- methods should be cancellation-aware;
- renderer events should not directly re-enter lifecycle methods;
- disposal must detach event handlers;
- native callbacks should be marshalled onto the engine thread when they change state.

---

## 6. Wallpaper Assignment Flow

Use prepare–swap–commit.

```text
User selects a wallpaper
        ↓
Validate source
        ↓
Probe media or validate web folder
        ↓
Increment assignment generation
        ↓
Create candidate renderer
        ↓
Initialize candidate while hidden
        ↓
Load candidate
        ↓
Candidate reaches Ready
        ↓
Verify generation is still current
        ↓
Attach or activate candidate
        ↓
Apply current performance state
        ↓
Commit assignment atomically
        ↓
Dispose previous renderer
```

Do not dispose the old renderer before the candidate is ready.

This preserves the current wallpaper when:

- the new file is corrupt;
- WebView2 initialization fails;
- the monitor is disconnected;
- the request is superseded;
- application shutdown begins;
- loading is cancelled.

Example generation guard:

```csharp
var generation = ++runtime.AssignmentGeneration;

var candidate = _rendererFactory.Create(definition);

await candidate.InitializeAsync(context, cancellationToken);
await candidate.LoadAsync(source, cancellationToken);

if (generation != runtime.AssignmentGeneration)
{
    await candidate.DisposeAsync();
    return;
}

await SwapAndCommitAsync(runtime, candidate, definition, cancellationToken);
```

---

## 7. Reason-Based Performance Policies

Recommended reasons:

```csharp
public enum PerformanceReason
{
    FullscreenCovered,
    Battery,
    BatterySaver,
    SessionLocked,
    DisplayOff,
    SystemSleeping,
    RemoteDesktop,
    UserPaused,
    RendererFault,
    ExplorerUnavailable,
    MonitorDisconnected
}
```

Each reason maps to a requested state.

Example:

```text
FullscreenCovered      → Suspended
SessionLocked          → Suspended
DisplayOff             → Suspended
SystemSleeping         → Suspended
UserPaused             → Suspended
BatterySaver           → Throttled
Battery                → Throttled
No active reasons      → Running
```

Effective state:

```csharp
private static PerformanceState CalculateEffectiveState(
    IEnumerable<PerformanceState> requestedStates)
{
    if (requestedStates.Contains(PerformanceState.Suspended))
        return PerformanceState.Suspended;

    if (requestedStates.Contains(PerformanceState.Throttled))
        return PerformanceState.Throttled;

    return PerformanceState.Running;
}
```

A monitor resumes only when every suspension reason has been cleared.

---

## 7.1 Debounce Policy

Do not debounce all events equally.

Apply immediately:

- session lock;
- system sleep;
- display off;
- explicit pause;
- application exit;
- monitor removal;
- Explorer host invalidation.

Debounce only unstable window-coverage changes:

- Alt+Tab;
- foreground changes;
- window movement;
- fullscreen enter and exit;
- display reconfiguration.

A fallback poll should rebuild the complete activity snapshot so missed events can be corrected.

---

## 8. Fullscreen and Window-Coverage Detection

Fullscreen detection should not depend only on style flags.

Recommended evaluation:

1. obtain the relevant foreground top-level window;
2. resolve its root owner where appropriate;
3. reject ignored window classes and application-owned windows;
4. reject minimized, cloaked, tool, shell, Desktop, and invalid windows;
5. read DWM extended-frame bounds;
6. intersect the window with each monitor rectangle;
7. calculate monitor coverage;
8. apply physical-pixel tolerance;
9. classify maximized-to-work-area separately.

Suggested threshold:

```text
Coverage >= 98%
and uncovered edge <= configured physical-pixel tolerance
```

Do not scan every visible window without considering foreground state or z-order. A fullscreen window that remains behind another application must not keep the monitor suspended incorrectly.

The application must exclude:

- its management window;
- its interaction overlays;
- its wallpaper hosts;
- Desktop shell windows;
- owned transient popups not representing the foreground application.

---

## 9. Web Wallpaper Rendering

## 9.1 Virtual Host Mapping

Each wallpaper should receive an isolated origin.

Example:

```text
https://wallpaper-{uuid}.vibe.local/index.html
```

This prevents wallpapers from unintentionally sharing:

- local storage;
- cookies;
- cached origin data;
- service-worker scope.

The source directory should be mapped read-only through WebView2 virtual host mapping.

---

## 9.2 Security Handlers

Register and enforce:

```text
NavigationStarting
NewWindowRequested
DownloadStarting
PermissionRequested
WebResourceRequested
ProcessFailed
ServerCertificateErrorDetected
BasicAuthenticationRequested
WebMessageReceived
```

Default policy:

- allow the mapped local wallpaper origin;
- block popup windows;
- block file downloads;
- block unexpected top-level navigation;
- deny camera;
- deny microphone;
- deny geolocation;
- deny notifications;
- deny clipboard permissions;
- deny external network access;
- deny unsupported URL schemes;
- do not inject native host objects.

When network access is enabled for one wallpaper, allow only the documented network schemes and continue blocking privileged or custom schemes.

Incoming web messages must be schema-validated.

---

## 9.3 Bootstrap API

Expose only a data-oriented object.

Example:

```javascript
window.vibeWallpaper = {
  version: 1,
  mode: "independent",
  canvas: {
    width: 3840,
    height: 1080
  },
  viewport: {
    x: 1920,
    y: 0,
    width: 1920,
    height: 1080
  },
  monitor: {
    id: "stable-monitor-id",
    dpiScale: 1.25
  },
  performance: {
    state: "running",
    fpsHint: 30,
    onBattery: false,
    batterySaver: false
  },
  clock: {
    monotonicMilliseconds: 123456
  },
  seed: 12345
};
```

No object should provide direct native filesystem, process, registry, shell, or application-control access.

---

## 9.4 Web Suspend and Resume

Recommended order:

```text
Suspend:
Controller becomes invisible
        ↓
Call TrySuspendAsync
        ↓
Record success or failure
```

```text
Resume:
Call Resume
        ↓
Restore controller visibility
        ↓
Resynchronize bootstrap state
```

If suspension fails, keep the renderer hidden when the policy requires it and record diagnostics.

---

## 9.5 Web Throttling

Do not assume WebView2 can force every arbitrary page to render at an exact target FPS.

Web throttling can include:

- send an FPS hint to compatible wallpapers;
- reduce passive pointer sampling;
- reduce application-originated update frequency;
- select a lower memory target where available;
- suspend incompatible pages when configured.

Third-party pages may ignore FPS hints.

Document throttling as cooperative or renderer-dependent.

---

## 10. Input and Interaction

## 10.1 Passive Mode

In passive mode:

- wallpaper hosts remain transparent to Desktop hit testing;
- Desktop icons behave normally;
- pointer position is sampled globally;
- sampling is limited, for example to 30 Hz;
- coordinates are converted into monitor and wallpaper space;
- only pointer movement is forwarded;
- click, wheel, keyboard, and text input are not forwarded.

When the pointer leaves a web renderer, send the corresponding pointer-leave event.

---

## 10.2 Interactive Mode

Do not rely only on removing a transparent-hit-test window style from a wallpaper behind Desktop icons.

Recommended design:

```text
InteractionOverlayWindow per active monitor
```

Flow:

```text
User presses configured hotkey
        ↓
Validate Desktop or management-window context
        ↓
Create transparent interaction overlays above Desktop icons
        ↓
Route pointer and keyboard input to InteractionManager
        ↓
Convert input into target WebView2 coordinates
        ↓
Forward through CoreWebView2CompositionController
        ↓
Move target when pointer crosses monitors
        ↓
Exit and destroy overlays
```

Exit conditions:

- `Esc`;
- session lock;
- display off;
- system sleep;
- Desktop context loss;
- application exit;
- inactivity timeout;
- renderer disposal;
- monitor topology change that invalidates the target.

Interaction overlays must:

- never appear in Alt+Tab;
- avoid activation where possible;
- be clearly owned by InteractionManager;
- be destroyed reliably in `finally`;
- never log typed key content.

A watchdog or timeout should restore normal Desktop input if the interaction path becomes unresponsive.

---

## 11. Multi-Monitor Display Modes

## 11.1 Invariant

Maintain:

```text
One physical monitor
    → one WallpaperHostWindow
    → zero or one active renderer
```

This remains true in independent, duplicate, and span modes.

It simplifies:

- per-monitor suspension;
- monitor hot-plug;
- mixed-DPI behavior;
- renderer ownership;
- failure recovery;
- assignment persistence.

---

## 11.2 Independent Mode

Each monitor has its own:

- wallpaper definition;
- renderer;
- fit mode;
- FPS setting;
- audio setting;
- performance reasons;
- lifecycle state.

---

## 11.3 Duplicate Mode

All selected monitors reference the same wallpaper definition, but each monitor has a separate renderer instance.

Advantages:

- each monitor can suspend independently;
- monitor failure remains locally recoverable where possible;
- no shared native presentation surface is required.

Duplicate-mode video renderers may share:

- logical start time;
- desired playback position;
- drift-correction policy.

They should not share one `MediaPlayer`.

---

## 11.4 Span Mode

Span mode uses one virtual canvas and one viewport per monitor.

Each renderer receives:

```text
Virtual canvas size
Monitor viewport offset
Monitor viewport size
Shared logical clock
Shared deterministic seed where applicable
```

### Video Span

Use:

- one monotonic logical playback clock;
- one master playback reference;
- drift measurement;
- correction when drift exceeds tolerance;
- immediate resynchronization after resume.

Multiple video decoders may not remain frame-perfect. Define an acceptable drift tolerance rather than promising exact frame synchronization.

### Web Span

Separate WebView2 instances have separate:

- JavaScript heaps;
- animation clocks;
- random state;
- loading timing;
- WebGL contexts.

Continuous web span therefore requires wallpaper cooperation through:

- shared canvas information;
- viewport offset;
- synchronized monotonic time;
- deterministic seed;
- compatible rendering logic.

Arbitrary pages should fall back to best-effort cover or contain behavior.

---

## 12. Video Renderer Details

A video renderer should:

1. create a LibVLC media player;
2. bind it to the monitor renderer HWND;
3. load media without exposing partial state;
4. default to muted audio;
5. enable hardware decoding when supported;
6. loop playback;
7. emit recoverable error events;
8. detach callbacks before disposal.

Audio behavior should be explicit in multi-monitor modes.

Recommended default:

```text
Only one selected renderer may produce audio.
All other duplicate or span renderers remain muted.
```

This prevents duplicated audio playback.

Video probing should occur before library acceptance and again if the source changes.

---

## 13. Imported Source Handling

## 13.1 Video Sources

Store:

- source path;
- file length;
- last-write timestamp;
- optional hash or fingerprint;
- detected dimensions;
- duration;
- nominal FPS;
- codec information when available;
- last validation result.

A changed file should be reprobed before activation.

---

## 13.2 Web Folder Sources

Store:

- source directory;
- `index.html` path;
- directory fingerprint;
- latest observed modification;
- network permission;
- validation version;
- last validation result.

A safe import can become unsafe after files in its directory change.

Recommended flow:

```text
Detect source change
        ↓
Mark wallpaper Changed
        ↓
Revalidate required entry point and paths
        ↓
Reset or reconfirm sensitive permissions if policy requires
        ↓
Reload only after validation succeeds
```

The application must never modify imported source files.

---

## 13.3 Missing Sources

Missing items remain in the library.

State:

```text
Available
Changed
Missing
Invalid
Unsupported
```

If an active source is missing:

- preserve the assignment;
- show a configured fallback;
- expose the missing state in the UI;
- automatically revalidate if the source later becomes available.

---

## 14. Failure and Recovery Model

## 14.1 Recoverable Renderer Fault

Examples:

- unsupported media;
- playback error;
- failed web navigation;
- a single controller becoming unresponsive;
- temporary attachment failure.

Behavior:

```text
Keep previous renderer when replacing
or
isolate the fault to the affected monitor where possible
        ↓
Retry after bounded delays
        ↓
Stop automatic retry
        ↓
Require manual retry
```

Suggested delays:

```text
1 second
2 seconds
5 seconds
```

---

## 14.2 WebView2 Process Failure

Web renderers sharing one environment may share browser-process dependencies.

A main WebView2 browser-process failure can require all web renderers using that environment to be recreated.

Document this explicitly:

```text
Single renderer-process fault
    → recreate affected web renderer when possible

Shared browser-process fault
    → recreate all web renderers using the environment
```

---

## 14.3 Native Process Failure

A fatal native LibVLC or interop crash may terminate the entire application process.

One-process architecture cannot guarantee complete fault isolation.

Accurate guarantee:

```text
Recoverable renderer errors are isolated per monitor where possible.
Fatal native failures may terminate the application.
```

True process isolation requires separate renderer host processes and inter-process communication.

---

## 14.4 Explorer Restart

Recovery flow:

```text
Detect Explorer or Desktop-host invalidation
        ↓
Mark cached Desktop handles stale
        ↓
Suspend or hide wallpaper hosts
        ↓
Keep assignments and renderer definitions
        ↓
Rediscover Desktop hierarchy
        ↓
Recreate or reattach host windows
        ↓
Restore monitor bounds
        ↓
Reapply renderer state
        ↓
Resume according to current policy
```

Never continue using stale HWND values after Explorer restarts.

---

## 14.5 Display Topology Change

Flow:

```text
Receive topology-change signal
        ↓
Debounce unstable intermediate topology
        ↓
Enumerate active monitors
        ↓
Reconcile stable identities
        ↓
Cancel operations for removed hosts
        ↓
Preserve disconnected assignments
        ↓
Create hosts for new monitors
        ↓
Restore matching assignments
        ↓
Recalculate span and duplicate groups
        ↓
Reapply fullscreen and power policies
```

A monitor removed during renderer initialization must invalidate that renderer operation through cancellation or assignment generation.

---

## 15. Startup Flow

Recommended order:

```text
1. Configure Per-Monitor-V2 DPI awareness.
2. Acquire a single-instance lock.
3. Initialize bounded diagnostics.
4. Load and migrate settings, library, and assignments.
5. Validate native runtime dependencies.
6. Validate WebView2 runtime availability.
7. Start the engine STA thread and message loop.
8. Discover the Desktop host hierarchy.
9. Enumerate and reconcile monitors.
10. Create one WallpaperHostWindow per monitor.
11. Restore assignments using prepare–swap–commit.
12. Start ActivityMonitor.
13. Register notification-area commands.
14. Register the global hotkey.
15. Open or hide the management window according to launch context.
```

DPI awareness must be configured before creating any HWND.

If startup restoration fails for one monitor, continue initializing the others and surface the failure non-blockingly.

---

## 16. Management Window Close Behavior

Closing the management window should:

```text
Hide the WinUI window
Keep VibeWallpaper.exe running
Keep wallpaper hosts and renderers active
Keep notification-area commands available
```

It must not be confused with explicit application exit.

---

## 17. Explicit Exit Flow

Recommended order:

```text
1. Reject new UI and engine operations.
2. Exit interaction mode.
3. Destroy all interaction overlays.
4. Unregister global hotkeys and hooks.
5. Stop ActivityMonitor.
6. Cancel pending probes, loads, and renderer replacements.
7. Stop and dispose active renderers.
8. Destroy WallpaperHostWindows.
9. Release DesktopHost attachments.
10. Remove notification-area icons.
11. Flush settings and bounded diagnostics.
12. Stop the engine message loop.
13. Exit the process.
```

Each shutdown operation should have a bounded timeout.

A hung WebView2 controller must not prevent the application from exiting indefinitely.

---

## 18. Portable Startup Registration

Use a per-user startup registration mechanism.

Possible choices:

- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`;
- per-user Startup-folder shortcut.

Requirements:

- no administrator privileges;
- clean removal from Settings;
- quote paths correctly;
- support Unicode paths;
- detect stale registration after the portable folder is moved;
- update registration when the current executable path changes.

Do not create a machine-wide startup entry.

---

## 19. Recommended Project Structure

```text
src/
├── VibeWallpaper.App/
│   ├── App.xaml
│   ├── AppShell/
│   ├── Views/
│   ├── ViewModels/
│   └── Tray/
│
├── VibeWallpaper.Application/
│   ├── Coordination/
│   ├── Commands/
│   ├── Policies/
│   └── Contracts/
│
├── VibeWallpaper.Engine/
│   ├── WallpaperEngine/
│   ├── DesktopHost/
│   ├── Monitors/
│   ├── Activity/
│   ├── Interaction/
│   ├── Rendering/
│   │   ├── Video/
│   │   └── Web/
│   └── Runtime/
│
├── VibeWallpaper.Infrastructure/
│   ├── Persistence/
│   ├── Diagnostics/
│   ├── MediaProbe/
│   ├── Thumbnails/
│   └── Native/
│
├── VibeWallpaper.Domain/
│   ├── Wallpapers/
│   ├── Monitors/
│   ├── Assignments/
│   ├── Policies/
│   └── States/
│
└── Tests/
    ├── VibeWallpaper.UnitTests/
    ├── VibeWallpaper.IntegrationTests/
    └── VibeWallpaper.WindowsTests/
```

Dependency direction:

```text
App
  → Application
  → Domain

Engine
  → Application contracts
  → Domain

Infrastructure
  → Application contracts
  → Domain
```

Domain code should not depend on WinUI, WebView2, LibVLC, or raw Win32 APIs.

---

## 20. Testing Recommendations

## 20.1 Unit Tests

Test:

- lifecycle transitions;
- performance-state precedence;
- reason addition and removal;
- idempotent state requests;
- fullscreen rectangle calculations;
- negative virtual-screen coordinates;
- mixed-DPI coordinate conversion;
- monitor identity reconciliation;
- assignment generation guards;
- configuration migration;
- atomic write and backup recovery;
- source-state transitions;
- web-message schema validation.

---

## 20.2 Integration Tests

Test:

- fake DesktopHost attachment and invalidation;
- renderer prepare–swap–commit behavior;
- candidate load failure preserving the old renderer;
- WebView2 virtual-host mapping;
- blocked navigation and permissions;
- WebView2 process-failure handling;
- LibVLC loop, pause, seek, mute, and disposal;
- hotkey conflict;
- interaction-mode exit conditions;
- Explorer handle invalidation;
- bounded retry policy.

---

## 20.3 Concurrency and Race Tests

Include:

- rapid wallpaper changes while probing;
- monitor removal during WebView2 initialization;
- Explorer restart during renderer swap;
- session lock during interaction mode;
- sleep during video loading;
- application exit during thumbnail generation;
- display change during span recalculation;
- old load completing after a newer assignment;
- repeated suspend and resume events;
- renderer fault during shutdown.

---

## 20.4 Windows Manual Matrix

Validate:

- clean Windows 11 installation;
- one monitor;
- two or more monitors;
- mixed DPI;
- negative coordinates;
- portrait monitor;
- primary-monitor change;
- hot plug;
- sleep and resume;
- display off and on;
- session lock and unlock;
- exclusive fullscreen;
- borderless fullscreen;
- maximized window with visible taskbar;
- Alt+Tab;
- Explorer restart;
- Remote Desktop;
- battery and battery saver;
- missing WebView2 runtime;
- unsupported or corrupt media;
- Unicode and long source paths.

---

## 20.5 Resource and Soak Testing

Measure:

- CPU while all wallpapers are suspended;
- GPU usage while suspended;
- video hardware-decoding behavior;
- WebView2 process count;
- private memory over extended use;
- handle count;
- timer and callback cleanup;
- renderer disposal;
- host-window disposal;
- repeated monitor hot-plug;
- repeated Explorer restart;
- eight-hour mixed video and WebGL workload.

No renderer, WebView2 controller, media player, hook, timer, or event subscription should remain after its monitor runtime is disposed.

---

## 21. Required Design Corrections

Before implementation, update the specification with the following decisions:

1. Pin a stable Windows App SDK version rather than relying on a floating version.
2. Keep WinUI 3 limited to the management interface.
3. Use native Win32 wallpaper host windows.
4. Use `CoreWebView2CompositionController` for web wallpaper rendering and synthetic input.
5. Add an explicit engine STA thread.
6. Serialize renderer transitions per monitor.
7. Separate renderer lifecycle from performance state.
8. Use assignment generations and cancellation to reject stale operations.
9. Use prepare–swap–commit for wallpaper replacement.
10. Debounce only unstable window-coverage events.
11. Recalculate the complete activity snapshot during fallback polling.
12. Consider foreground state and z-order in fullscreen detection.
13. Use interaction overlays for active click and keyboard routing.
14. Treat web throttling as cooperative or renderer-dependent.
15. Treat arbitrary web span behavior as best effort.
16. Define a stable monitor-identity reconciliation algorithm.
17. Document WebView2 shared-process recovery behavior.
18. Document that fatal native failures can terminate the one-process application.
19. Revalidate imported files and directories when their contents change.
20. Add bounded timeouts to shutdown and renderer recovery.
21. Validate external runtime dependencies at startup.
22. Repair stale startup registration when a portable installation is moved.
23. Add race-condition tests and clean-machine deployment tests.

---

## 22. Final Conclusion

The selected technology stack is appropriate:

```text
C# / supported .NET LTS
WinUI 3 for management UI
Win32 + DWM + COM for Desktop integration
WebView2 for HTML and WebGL wallpapers
LibVLCSharp for video wallpapers
System.Text.Json for local persistence
xUnit plus Windows integration and manual testing
```

The module boundaries are also appropriate, especially the separation between:

```text
AppShell
DesktopHost
MonitorManager
WallpaperEngine
ActivityMonitor
InteractionManager
Renderers
Persistence
Diagnostics
```

The main implementation risks are not the selected technologies themselves. The major risks are:

- asynchronous renderer races;
- Desktop HWND invalidation;
- WebView2 thread affinity;
- active input routing behind Desktop icons;
- shared WebView2 process failures;
- native-process failures;
- monitor identity instability;
- span synchronization;
- incomplete cleanup during shutdown.

With the state model, threading rules, operation serialization, recovery flows, and security rules defined in this document, the design is sufficiently coherent to begin implementation.
