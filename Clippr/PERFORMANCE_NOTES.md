# ClipDat performance pass

Date: 2026-10-13

## Changes

- When replay buffering and manual recording consume the same WGC frame, the capture callback now makes one retained D3D11 texture copy and gives a COM reference to each independently bounded queue. This cuts the queue-input copies from two to one per frame (50% fewer copies in the dual-consumer case). Frames remain GPU textures; no video frame is read back to CPU memory.
- The recording and replay queues remain bounded at two frames each. A full queue drops the incoming frame and increments its counter; capture does not wait for the encoder to catch up.
- Development builds show capture FPS, replay encoded FPS, dropped frames, replay RAM, queue wait, encoder write latency, active hardware encoder, and last save duration. Native counters use atomics and steady-clock samples; the overlay is compiled out of Release builds.

## Measurements

Measurements were taken on the same Windows machine with the app buffering a desktop at source resolution. CPU and memory samples were collected for about 10 seconds before and after; GPU engine counters were sampled for several seconds. The desktop was not a repeatable game workload. Windows Graphics Capture is event driven, so FPS and GPU load vary with visible desktop changes.

| Metric | Before | After | Reading |
| --- | ---: | ---: | --- |
| ClipDat CPU, normalized across logical processors | 1.11% | 1.09–1.41% | No attributable improvement in these short, low-load samples |
| Working set | about 240 MB | 240–248 MB | Similar range; higher reading followed a restart and warm-up |
| GPU video codec engine | 1.04% | 0.63% in one sample | Lower in that sample; frame arrival and desktop activity varied, so not attributed to the copy change |
| GPU 3D engine | 0.20% | 0.01% in one sample | Lower in that sample; not treated as a controlled improvement |

After the change, live telemetry ranged from 2.8 to 16.9 captured FPS as visible desktop activity changed. In the later 10-second sample it showed 16.0 encoded FPS, 2 queue drops since startup (the count did not rise during that sample), 10.1 MB replay RAM, 2.5 ms average queue wait / 601.6 ms maximum, and 0.9 ms average encoder write / 46.0 ms maximum. The maximum queue wait was a startup outlier; subsequent average waits were much lower. One real 20-second MKV export took 22 ms in the exporter. An exact pre-change save-duration value was not available, so no before/after export-latency improvement is claimed.

## Inspection notes

- Capture uses WGC D3D11 textures and the video encoder's D3D11 video processor to convert frames for hardware H.264. The video path contains no CPU `Map`/staging-texture readback.
- WGC frames drive preview drawing and swap-chain presentation; the app does not run a separate high-frequency preview render loop. The preview still creates a D2D bitmap wrapper and presents each arriving capture frame.
- Captured timestamps use WGC's 100 ns system-relative timeline. WASAPI timestamps are rebased onto that timeline before packets enter the replay buffer.
- Replay packets remain compressed and bounded by configured RAM and disk limits. Snapshotting copies packet references and metadata; export runs on a save worker, with at most three active saves.
- Encoder and WASAPI workers are stopped and joined during shutdown. Active save workers are joined before the capture component is destroyed. Window close can therefore wait for an in-progress export to finish.
- Disk spill currently flushes each packet while the replay-buffer lock is held. This is safe for snapshots but can increase latency after the RAM limit is reached on slower storage. Moving spool writes off the replay encoder thread needs a separately bounded writer queue and backpressure policy, so it remains a follow-up rather than risking long-session reliability in this pass.

## Verification

- `dotnet build Clippr.csproj -p:Platform=x64 -p:Configuration=Debug --no-restore -v:q` succeeded with zero errors. Existing FFmpeg header conversion warnings remain.
- `native/build/x64/replay_buffer_tests.exe` passed.
- A real 20-second MKV replay export completed successfully after the change; the measured export time is shown above.
