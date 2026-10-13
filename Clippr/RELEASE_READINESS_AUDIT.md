# ClipDat production readiness audit

Audit date: 2026-10-13

Repository: ClipDat source tree

## Release decision

**Not ready for public production release.** The x64 application builds in Debug and Release, the replay-buffer test suite passes, and real H.264/AAC MKV clips decode and seek. One of two existing clips did not meet the replay verifier's 100 ms end-of-stream audio/video timestamp limit (129 ms). The public Windows release is also blocked by the development package publisher (`CN=ryand`), absence of a trusted release signature, and unverified clean-machine install, upgrade, repair, and Windows App SDK dependency flows.

No code changes were made during this audit: no compilation errors or new security flaw were confirmed. Changing audio timestamp behavior to mask one older clip's stream-end delta without a controlled capture would be unsafe.

## Implemented and tested

- **Build:** x64 Debug and Release builds complete with zero errors. Release reports 11 warnings: three unused diagnostic fields in C# and eight narrowing warnings in bundled FFmpeg headers. These are warnings, not build failures.
- **Replay buffer:** `native/test-replay.cmd` passes the current wraparound, keyframe, bounded RAM/disk, free-space reserve, packet timestamp, independent audio-track metadata, and concurrent snapshot/save tests.
- **Real file decoding:** `native/verify-replay.cmd` opened two saved MKV files. Both contain H.264 video and AAC audio, decode through the end, and seek to the middle. The latest file passed the check with 76 ms end delta; the older file decoded and sought but returned failure because its end delta was 129 ms (limit: 100 ms). Start deltas were 1 ms and 20 ms.
- **H.264 playback:** `native/verify-recording.cmd` decoded the latest saved H.264 video to its end: 53 frames at 1920×1200 over 27.02 seconds.
- **Machine observed:** one Windows x64 system with an AMD GPU. Exact host and driver identifiers are omitted from this repository. This does not establish compatibility with other Windows builds or GPU vendors.
- **Read-only process sample:** over 6.1 seconds, the currently running process used about 17.8% of one logical core. Working set changed from 203.9 MB to 202.4 MB, private bytes from 245.3 MB to 243.9 MB. Its recording workload was not controlled, so this is not a recording overhead benchmark or evidence of long-session memory stability.

## Implemented but not tested in this audit

These features have implementation evidence in the source, but this audit did not execute the corresponding live interaction or failure injection:

- Windows Graphics Capture for monitor and HWND sources, real-time preview, source switching, and resizing. Code uses WGC and Direct3D 11; source inspection is not a live preview test.
- Media Foundation H.264 hardware encoder selection, which rejects a transform without the hardware URL attribute. H.264 file decoding does not prove which encoder produced a file or that NVIDIA and Intel encoders work.
- WASAPI desktop loopback and microphone capture, device enumeration, process loopback tracks, AAC encoding, and muxer support for separate audio streams. No microphone capture was run in this audit.
- Screenshot PNG/JPEG pipeline, global hotkeys, native toast and in-app notifications, notification throttling, shell file actions, JSON settings round-trip, and the tray menu/minimize/restore/exit flows.
- Display/audio loss recovery, sleep/resume, disk-full behavior, device removal, GPU reset, and repeated save/export during active capture.
- MP4 export. The files available to validate in this audit were MKV only.
- Public-package install, upgrade, repair, and uninstall. Packaging documentation records that a local uninstall preserved settings and clips, but also records that trusted installation was rejected and clean-machine flows remain unverified. That historical note was not repeated in this audit.

## Partially implemented

- **Direct sharing:** public peer-to-peer sharing is intentionally unavailable. The UI reports this, does not create or copy a public URL, and does not probe external connectivity. `LocalClipShareServer` can serve only a selected file on `127.0.0.1`, using a random 256-bit token, range requests, expiry/revocation, and bounded connections; the public share UI does not start it. Public HTTPS, external reachability, multiple public links, and external-link expiration/revocation do not exist. No router mapping or public listener was found.
- **Installer:** the deployment model is MSIX with per-user helper scripts, Windows-managed Start Menu/Installed Apps registration, and a sidecar Windows App SDK runtime dependency. This matches the project model, but production signing and end-to-end clean-machine deployment evidence are missing. `CN=ryand` is development publisher metadata. MSIX does not support choosing the install directory; the release package is x64 only. The project file lists x86/ARM64 platforms, but the native DLL and FFmpeg deployment are x64-only.
- **Audio/video sync:** timestamps are preserved and the latest clip meets the verifier tolerance. One older clip exceeds the end tolerance by 29 ms. Need repeatable captures of each enabled audio combination before deciding whether this is clock drift or normal stream-end duration skew.
- **Performance:** `PERFORMANCE_NOTES.md` contains short before/after samples and a post-change telemetry sample. Its own caveats say the workload was not repeatable and that no improvement is attributed to the variable GPU readings. This audit's brief process sample is likewise uncontrolled. GPU per-process counters, long-duration memory growth, encode backpressure, and repeat-save latency were not measured here.
- **Platform and vendor coverage:** only one Windows 10 25H2 x64 machine with an AMD GPU is present. Windows 10 1809, other supported Windows releases, NVIDIA, and Intel cannot be certified from this host. Process loopback is gated in code on Windows build 20348 or newer, so that optional audio source is unavailable on older supported Windows builds.

## Not implemented

- Public direct clip sharing that works in an ordinary remote browser under the user's no-relay/no-hosted-service constraints. The app accurately reports this limitation.
- A production-trusted signed release package and verified clean-machine install/upgrade/repair test record.
- Verified support results for Windows 10 1809 through current Windows 11 releases, NVIDIA and Intel hardware encoders, or ARM64/x86 release builds.
- Controlled long-duration capture benchmarks and automated failure-injection coverage for device loss, driver reset, sleep/resume, low disk, microphone denial, and concurrent real exports.

## Security review

- The only listener located is created on demand and bound to loopback. It has a random 32-byte token, maps each token to one selected file, supports no directory listing, and applies connection/header/write limits. It is not a public sharing service and the UI does not expose its URL as externally reachable.
- No UPnP, NAT-PMP, PCP, firewall rule modification, public reachability probe, or relay/upload path was found in the reviewed first-party source.
- Clip export writes to a unique temporary file, finalizes the container, then renames it into place; failure cleanup deletes the temporary file. Screenshot export follows a temporary-file/finalization path as well.
- The package requests microphone and full-trust capabilities. No unrelated service or browser framework was identified.
- This was a source/configuration review, not a penetration test or complete secrets scanner. No embedded credential or private-key evidence was found in the reviewed configuration. Do not treat that as a cryptographic binary scan.

## Release work still required

1. Resolve the older MKV A/V end-delta failure with a controlled capture, then verify MP4/MKV, all enabled tracks, stream duration, and seek behavior on fresh captures.
2. Run live tests for screen/application capture, preview, screenshots, hotkeys, settings persistence, tray behavior, notifications, shell actions, and orderly shutdown.
3. Test extended recording and repeated saves while recording; record CPU/GPU usage, private working-set growth, dropped frames, encoder latency, and audio/video drift under a repeatable workload.
4. Test on supported Windows versions and hardware encoder vendors. Document feature fallback where hardware encoding or process loopback is unavailable.
5. Set the intended public publisher identity, sign with a trusted release certificate, and verify fresh install, dependency installation without a preinstalled Windows App SDK runtime, upgrade, repair, and uninstall on a clean machine.
6. Keep direct sharing disabled unless a compliant, externally reachable HTTPS design becomes available; do not label loopback URLs as share links.
