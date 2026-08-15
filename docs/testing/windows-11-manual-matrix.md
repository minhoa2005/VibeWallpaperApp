# Windows 11 Manual Matrix

Record OS build, WebView2 version, LibVLC files, monitor topology/DPI and date for each run.

## Plan 1 / Plan 2 Task 0 release checkpoint

Every checkpoint row must name the exact build under test and the directory or file that contains the evidence.

Observed release build for this checkpoint on Friday, August 14, 2026: `artifacts/portable/VibeWallpaper` after `eng/verify-portable.ps1` succeeded.

The original `artifacts/performance/plan1-4k60-one-monitor/` capture remains diagnostic evidence only. The corrected v2 capture under `artifacts/performance/plan1-4k60-one-monitor-v2/` is the current release-gate baseline evidence: `BaselineValidationOnly` passes with 301 process rows, CSV span 300.0197s, `GpuSampleCount=300`, `GpuCountersAvailable=True`, 300 merged GPU rows, one unmatched leading process row, and max merged GPU age 985ms.

### Current checkpoint status

| Scenario | Result | Build identity | Evidence path |
| --- | --- | --- | --- |
| Release-gate baseline requirement (`samples.csv` + `summary.json`) | Pass: corrected v2 capture exists and `BaselineValidationOnly` passes. Artifact has 301 process rows, span 300.0197s, `GpuSampleCount=300`, GPU available true, 300 merged GPU rows, one unmatched leading row, and max GPU age 985ms. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/performance/plan1-4k60-one-monitor-v2/` |
| Three real loops (single-monitor continuity) | Blocked by missing live app session and missing manual evidence for this checkpoint. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/manual/three-real-loops/` |
| 100-loop soak | Blocked by missing live app session and missing manual evidence for this checkpoint. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/manual/100-loop-soak/` |
| One-monitor 4K60 video | Pass for the release-gate baseline capture: corrected v2 artifact exists and validates. The earlier non-v2 capture remains diagnostic history only. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/performance/plan1-4k60-one-monitor-v2/` |
| Two-monitor same-video 4K60 | Future manual run required after the one-monitor baseline exists. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/performance/plan1-4k60-two-monitor/` |
| One fullscreen output, one visible output | Future manual run required after the one-monitor baseline exists. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/performance/plan1-one-fullscreen-one-visible/` |
| All outputs fullscreen | Future manual run required after the one-monitor baseline exists. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/performance/plan1-all-fullscreen/` |
| Resource artifacts archived (`samples.csv`, `summary.json`, screenshots, notes) | Baseline performance artifacts are present for the corrected one-monitor v2 capture. Other manual evidence rows remain pending as listed above. | `artifacts/portable/VibeWallpaper` verified on Friday, August 14, 2026 | `artifacts/performance/` |

### Future manual matrix rows

Use these same evidence paths for future reruns after a live `VibeWallpaper.App` session is available.

## Additional matrix

| Scenario | Result | Evidence |
| --- | --- | --- |
| Explorer restart and WorkerW rediscovery | Pending interactive run | |
| Hot-plug / unplug during assignment | Pending interactive run | |
| WebView2 network off/on and origin isolation | Capability-gated | |
| Interaction pointer, keyboard, focus and Esc cleanup | Capability-gated | |
| Light/dark/high-contrast dashboard and settings | Pending interactive run | |
| Portable launch without elevation | Scripted by release gate | |
| Task 21 — Library import/state/apply workflow | Pass (automated) | Focused suite plus `SourceIntegrityWorkflowTests`; import is immediately resolvable from the runtime snapshot. |
| Task 21 — Picker filters/cancellation/error notices | Pass (automated seams) | Exact `.mp4/.webm/.mkv/.mov/.gif` filters, HWND ownership, silent cancellation, typed error mapping. |
| Task 21 — Navigation, focus, picker and InfoBar on a running window | Capability-gated | `winapp run` 0.3 requires `Package.appxmanifest`; this app is intentionally unpackaged (`WindowsPackageType=None`). No direct EXE launch was used. |
| Task 21 — Light, Dark and High Contrast visual inspection | Capability-gated | Static theme-resource/analyzer review passed; running-window visual evidence unavailable under the same launch gate. |
