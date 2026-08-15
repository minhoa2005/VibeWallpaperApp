# Plan 1 4K60 One-Monitor Baseline Analysis

**Capture:** `artifacts/performance/plan1-4k60-one-monitor/`  
**Capture time:** 2026-08-15 11:50:30Z–11:55:29Z  
**Backend:** LibVLC fallback  
**Source duration from runtime diagnostics:** 11.267 seconds

## Artifact validity

The capture is useful diagnostic evidence but does not pass the Plan 1 release
gate. `samples.csv` contains 118 rows. The first and final elapsed values are
2.1600 and 301.3187 seconds, producing a measured CSV span of 299.1587 seconds.
The gate requires at least 300 seconds and rejects this artifact.

The requested interval was one second, but the observed intervals are:

| Metric | Value |
| --- | ---: |
| Minimum interval | 2.451 s |
| Maximum interval | 2.776 s |
| Average interval | 2.557 s |
| Samples collected | 118 |

The current sampler sleeps for one second and then performs synchronous
`Get-Counter` work. That counter call contributes another roughly 1.5 seconds,
so the actual process-sampling cadence is about 0.39 Hz instead of 1 Hz. The
final counter call finishes after the stopwatch crosses 300 seconds, while its
sample timestamp is still before the required terminal boundary.

## Resource findings

| Metric | Initial | Final | Minimum | Maximum | Average |
| --- | ---: | ---: | ---: | ---: | ---: |
| Private bytes | 327.9 MiB | 316.5 MiB | 242.4 MiB | 509.2 MiB | 405.9 MiB |
| Working set | 367.8 MiB | 358.1 MiB | 284.3 MiB | 550.9 MiB | 446.6 MiB |
| Handles | 1496 | 1497 | 1479 | 1502 | 1489.3 |
| Threads | 84 | 81 | 79 | 85 | 80.6 |
| CPU | 0% | 1.03% | 0% | 2.68% | 0.76% |
| GPU Video Decode | 50.45% | 55.92% | 0% | 64.01% | 53.73% |
| GPU 3D | 0.54% | 0.34% | 0% | 6.31% | 0.54% |

The least-squares private-byte slope is approximately 1.84 MiB/minute, but it
must not be interpreted as a confirmed leak from this short run. Final private
bytes are lower than initial private bytes, while large periodic allocation and
release cycles dominate the regression. Handles and threads show no monotonic
growth in this capture.

`GpuCountersAvailable` is false. Most rows contain GPU values, but at least one
counter read failed, so the GPU figures are exploratory and cannot satisfy a
hardware release gate.

The fallback `PresentedFrames` value is currently a count of LibVLC
`TimeChanged` progress callbacks, not decoded or swap-chain-presented frames.
It therefore cannot establish real 4K60 frame delivery or quantify the visual
pause at a loop boundary. Plan 2 presenter/session counters remain necessary.

## Loop-boundary correlation

The active source duration is 11.267 seconds. Using the active renderer's
`play` timestamp and that duration, 26 loop boundaries fall inside the usable
capture window. Twenty-five of those boundaries are followed within the next
one-to-seven seconds by a private-memory increase greater than 100 MiB:

| Correlation metric | Value |
| --- | ---: |
| Boundaries evaluated | 26 |
| Boundaries followed by >100 MiB rise | 25 |
| Match rate | 96.2% |
| Median private-memory rise | 180.3 MiB |
| Maximum private-memory rise | 266.0 MiB |
| Median observed offset | 4.4 s |

The offset is limited by the coarse 2.557-second sampling cadence and by the
time required for native allocations to accumulate. The recurring interval of
the rises alternates around 10–13 seconds, which is the sampled form of the
11.267-second media duration.

The runtime log contains no `native-end`, `loop-progress`, `loop-recovery`, or
fault event during the capture, and every ten-second metrics snapshot reports
`LoopGeneration = 0`. It also contains no per-loop `open`, `play`, `stop`, or
`dispose`. Therefore the application is not recreating its renderer/player at
each boundary; LibVLC native repeat wraps playback timestamps without raising
the callback currently used for loop telemetry.

## Root-cause assessment

The strongest current hypothesis is that LibVLC 3 native repeat rebuilds or
rotates a native decoder/output surface pool at each loop boundary. A 4K BGRA
surface is roughly 31.6 MiB, so the median 180.3 MiB swing is consistent with
several full-resolution surfaces. This identifies the likely ownership layer,
but it is still an inference because the current diagnostics do not expose
LibVLC decoder-pool allocation events.

The perceptible stutter and memory churn are therefore architectural limits of
the fallback HWND pipeline rather than evidence of per-loop renderer creation.
GPU 3D peaks at 6.31%, but the failed/coarse GPU sampling is insufficient to
attribute each peak to an exact loop boundary.

## Consequences for the next plan

1. Repair sampler cadence and GPU collection, then capture a non-overwriting v2
   baseline that passes the five-minute gate.
2. Detect duration-boundary timestamp wraps so fallback loop generation and
   loop progress are truthful even when `EndReached` is absent.
3. Continue with the shared Media Foundation/D3D11 backend. Allocate the Media
   Engine, D3D device, texture ring, and swap chains once and prove their create
   counts remain unchanged across 100 loops.
4. Add loop-specific acceptance gates for frame-gap continuity and per-loop
   private-memory swing, rather than relying only on five-minute averages.

The executable plan is
`docs/superpowers/plans/2026-08-14-shared-media-foundation-d3d11-video.md`,
starting at Task 0.
