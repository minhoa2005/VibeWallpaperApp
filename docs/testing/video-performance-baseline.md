# Video Performance Baseline Protocol

## Goal

Collect consistent five-minute resource baselines for `VibeWallpaper.App` after a one-minute warmup so Task 5 soak artifacts can be compared across runs and machines. Plan 2 Task 0 uses the corrected v2 sampler, which keeps process sampling on fixed one-second deadlines and collects GPU counters independently.

## Required metadata

Record this metadata with every baseline run:

- Date and local time
- Build identity for the exact portable build under test
- Windows edition and OS build
- GPU model and installed driver version
- CPU model
- Monitor count, resolution, refresh rate, and topology (Independent, Duplicate, Span)
- Which outputs share the same adapter and which do not
- Video source file name, codec, resolution, and nominal cadence
- Backend in use (`LibVLC fallback` or later optimized backend)
- Evidence path that contains `samples.csv`, `summary.json`, screenshots, and notes for the scenario

## Sampling protocol

1. Launch the portable app manually and confirm the intended wallpaper assignment is stable.
2. Select the exact `VibeWallpaper.App` process for the launch under test and confirm the process start time matches the app instance you just opened.
3. Warm up for exactly one minute before collecting artifacts.
4. Run `eng/measure-video-performance.ps1` for a five-minute capture using that exact process ID, `-DurationSeconds 300`, and `-IntervalSeconds 1`.
5. Preserve both `samples.csv` and `summary.json` with the metadata above, plus screenshots or notes that prove the scenario. The sampler always writes raw evidence; release status is decided only by `eng/run-release-gate.ps1`.
6. Note any anomalies such as Explorer restart, monitor hot-plug, display sleep, fullscreen transitions, or backend fallback.

The v2 evidence contract requires `ObservedDurationSeconds`, `MaximumProcessSampleGapSeconds`, `ProcessSampleCount`, `GpuSampleCount`, and `GpuCountersAvailable` in `summary.json`, plus `GpuCountersAvailable` and `GpuSampleAgeMilliseconds` in every `samples.csv` row. Hardware release evidence requires merged GPU samples within 1.5 capture intervals; exploratory runs may preserve partial GPU evidence but must not be treated as a release pass.

If no `VibeWallpaper.App` process is available, stop and record that blocker in the task report and manual matrix. Do not launch or terminate an unrelated process to manufacture a sample.

## Release checkpoint evidence map

| Manual matrix row | Scenario | Output directory | Required evidence |
| --- | --- | --- | --- |
| Three real loops (single-monitor continuity) | Manual playback continuity validation for three wraps | `artifacts/manual/three-real-loops/` | Notes or screenshots plus build identity |
| 100-loop soak | Long-running playback continuity validation | `artifacts/manual/100-loop-soak/` | Notes or screenshots plus build identity |
| One-monitor 4K60 video | One monitor playing the selected 4K60 loop | `artifacts/performance/plan1-4k60-one-monitor-v2/` | `samples.csv`, `summary.json`, build identity, scenario notes |
| Two-monitor same-video 4K60 | Two monitors playing the same 4K60 loop | `artifacts/performance/plan1-4k60-two-monitor/` | `samples.csv`, `summary.json`, build identity, scenario notes |
| One fullscreen output, one visible output | One display covered by fullscreen while another remains visible | `artifacts/performance/plan1-one-fullscreen-one-visible/` | `samples.csv`, `summary.json`, build identity, scenario notes |
| All outputs fullscreen | All displays covered by fullscreen applications | `artifacts/performance/plan1-all-fullscreen/` | `samples.csv`, `summary.json`, build identity, scenario notes |
| Resource artifacts archived | Final collection check across all scenario folders | `artifacts/performance/` | Directory listing or archive manifest plus build identity |

## Example capture command

```powershell
$measuredProcessId = [int](Read-Host 'Enter the exact VibeWallpaper.App PID')
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\measure-video-performance.ps1 `
  -ProcessId $measuredProcessId -DurationSeconds 300 -IntervalSeconds 1 `
  -OutputDirectory artifacts\performance\plan1-4k60-one-monitor-v2 `
  -Scenario 4k60-one-monitor
powershell.exe -NoProfile -ExecutionPolicy Bypass -File eng\run-release-gate.ps1 `
  -BaselineDirectory artifacts\performance\plan1-4k60-one-monitor-v2
```

The sampler rejects invalid capture targets:

- Missing or zero PIDs fail when the process cannot be resolved.
- Exited or PID-reused processes fail when the sampled identity no longer matches the original start time and process name.

## Baseline scenarios

Capture all of the following scenarios:

- Idle solid color wallpaper
- One monitor playing a 1080p30 video
- One monitor playing a 4K60 video
- Two monitors playing the same 4K60 video
- One output covered by a fullscreen application while another output stays visible
- All outputs covered by fullscreen applications

## Interpretation notes

- Compare `PrivateBytesSlopeBytesPerMinute` across runs instead of looking only at the final sample.
- Review handle and thread growth alongside private bytes to distinguish leaks from cache growth.
- Treat GPU Video Decode and GPU 3D as scenario-dependent counters; they should be near zero when every output is covered.
- Keep raw artifacts from any suspicious run even if the summary looks normal, because short spikes are easier to spot in `samples.csv`.
- Preserve `artifacts/performance/plan1-4k60-one-monitor/` as the original Plan 1 diagnostic capture. Do not overwrite it with v2 evidence.
