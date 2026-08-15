# Task 21 — Library Import, Assignment, and Error UX Verification

## Environment

- Date/time: 2026-08-11 20:31 (UTC+07:00)
- OS: Windows 11 Home Single Language, 10.0.26200, x64
- WebView2 assembly: 1.0.4078.44
- LibVLC: 3.0.23.0, pinned x64 runtime
- Display evidence reported by Windows:
  - Intel UHD path: 1920 × 1080 at 144 Hz
  - NVIDIA RTX 3050 Laptop GPU path: 2560 × 1440 at 180 Hz
  - Active monitor PNP IDs: `DISPLAY\BOE08B3\4&24EA6B94&2&UID8388688`, `DISPLAY\XMI27B2\5&701041&0&UID4352`

## Automated verification

| Scenario | Result | Evidence |
| --- | --- | --- |
| Library authority, import preparation, controller, picker/dialog, ViewModel, runtime wiring, management Apply | Pass | Focused Task 21 test command; zero failed and zero skipped. |
| Debug solution build | Pass | `dotnet build VibeWallpaper.sln -c Debug --no-restore --nologo`; zero warnings and zero errors. |
| Windows App SDK analyzer build | Pass | `BuildAndRun.ps1 -SkipRun`; zero warnings and zero errors after flattening InfoBar bindings and enabling nullable annotations. |
| Full regression suite | Pass with one intentional capability skip | 527 total: 526 passed, 0 failed, 1 skipped. The skipped test is `DesktopHostIntegrationTests.CapturedOutputs_CreateDesktopChildrenAtExactPhysicalBounds_AndDisposeThem`; it requires explicit `VIBE_WALLPAPER_RUN_WINDOWS_INTEGRATION=1` because it changes the desktop. |
| Import is immediately assignable | Pass | `LibraryRuntimeWiringTests.ImportedWallpaper_IsImmediatelyResolvableAndAssignableFromRuntimeSnapshot`. |
| Unexpected/cancelled/repeated Apply | Pass | Stable `wallpaper.apply.failed`, silent caller cancellation, busy reset, and concurrent-call gate are covered in `TrayAndManagementUiTests`. |
| Exact video filters and picker ownership | Pass | Adapter receives `.mp4`, `.webm`, `.mkv`, `.mov`, `.gif`, multi-select, and management HWND. |
| Removal does not delete the source | Pass | Dialog text and the end-to-end source-integrity workflow both verify preservation. |

## Source-integrity workflow

`SourceIntegrityWorkflowTests` runs import → revalidate → web network on/off → Independent Apply → removal against a Unicode/long-path video fixture and a local web directory. The probe boundary is deterministic for the text-backed video fixture; assignment and persistence use the real runtime authority and fake renderer boundary. SHA-256, byte length, and last-write UTC are identical before and after.

| Relative path | SHA-256 | Bytes | Last-write UTC |
| --- | --- | ---: | --- |
| `Nguồn Unicode đường dẫn rất dài 0123456789 0123456789 0123456789 0123456789\bầu trời thử nghiệm.mp4` | `AF7B62EF64ABBEEE2A7903CA19D14680D911A832CF8F18DA8DDC18DAC2602964` | 125 | 2026-08-11T13:29:43.3016426Z |
| `Web cảnh biển\app.js` | `16ADE128BA649A5F68654C05ECBCEDA857D796CB7D036442E8FD9CD0A9C9F224` | 58 | 2026-08-11T13:29:43.3111597Z |
| `Web cảnh biển\index.html` | `71C30FB3AD0F9F7B8AB11448A14A6E8B827DC69FEACFDBDE414892D1B897ACAA` | 201 | 2026-08-11T13:29:43.3101545Z |

## Running-window/UI automation gate

The app is intentionally unpackaged (`WindowsPackageType=None`) and has a Win32 `app.manifest`, not a `Package.appxmanifest`. The installed `winapp run` command only creates and launches packaged loose layouts and failed with “Manifest file not found.” The workflow rule prohibits launching the EXE directly, and packaging was not changed merely to bypass the test harness.

Therefore these checks remain capability-gated rather than marked pass:

- keyboard navigation and visible focus between **Màn hình** and **Thư viện**;
- live OS video/folder pickers and picker cancellation;
- live InfoBar rendering for corrupt/missing sources;
- live Use-wallpaper navigation and real monitor rendering for Independent/Duplicate/Span;
- ContentDialog rendering and source-preservation copy;
- Light, Dark, and High Contrast screenshots.

Static/analyzer checks confirm AutomationIds/names, explicit binding modes, typed data templates, theme resources, no nested `ScrollViewer`, and no direct business logic in the page code-behind. These checks are supporting evidence, not substitutes for the capability-gated visual run.
