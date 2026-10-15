# ClipDat

ClipDat includes a Clip Library page in the main window for saved clips and screenshots. The older [standalone ClipDat Library](ClipDat.Library/README.md) remains available as a separate optional application, but the main app no longer launches it. ClipDat's **Quick Share** settings page explains the current direct-sharing status; no public link is offered without a verified secure endpoint.

**Downloads and setup:** release builds are published, once signing and installation checks are complete, on [GitHub Releases](https://github.com/TaylorDawson911/ClipDat/releases/latest). See the [installer guide](installer/README.md) for first-run modes, signing requirements, and package validation. Source builds alone are not an installed release.

ClipDat is a native Windows clipping and instant-replay desktop application built from the Clippr project. It is designed to keep capture and replay data on the user's PC and to use Windows capture, audio, encoding, and shell APIs.

> **Development status:** This is an active, pre-release project. Some features are implemented but have not been verified on all Windows versions, hardware vendors, or failure conditions. See [Testing and known limitations](#testing-and-known-limitations) before relying on it for important recordings.

## Features

- Capture a selected display or application window with Windows Graphics Capture and show a live preview.
- Encode H.264 through a runtime-detected Media Foundation hardware transform when a compatible encoder is available.
- Keep a bounded instant replay buffer of encoded packets, with configurable duration and memory limits.
- Save replay clips as MP4 or Matroska (MKV), and save screenshots as PNG or JPEG.
- Capture desktop audio and microphone audio with WASAPI. Optional process audio tracks depend on Windows support and the selected source.
- Configure capture, audio, output, notifications, hotkeys, and voice commands in the in-window settings page. Settings are stored as JSON under LocalAppData.
- Use native Windows notifications, a system tray icon, shell file actions, global hotkeys, and an MSIX package.
- Optionally enable constrained Windows speech commands. Speech recognition is opt-in, uses a local list grammar, and does not save or upload microphone audio.
- Optionally organise replay clips into game folders using lightweight process identity sampled during the replay interval. Both the main Clip Library and optional standalone Library discover nested folders.

### Game folders

Open **Settings → Output → Automatic organisation** to enable or disable game folders, choose a detection method and fallback folder, and add custom executable mappings. New installations enable this by default; existing installations retain their flat clip folder until they opt in. Existing files are not moved. ClipDat recognises selected known game executables or user mappings; it does not label every foreground program as a game. If the buffered interval is mixed or lacks enough samples, the clip goes to the selected fallback (normally `Other`). Separate direct recording file organisation is not available in this build.

## Compatibility

- Windows 10 version 1809 (build 17763) or later; Windows 11 is included.
- The currently supported and packaged build is **x64**. The project file contains other platform configurations, but the native capture DLL and FFmpeg runtime are x64-only.
- A compatible GPU and Media Foundation hardware H.264 transform are required for hardware encoding. Availability is detected at runtime; support depends on the installed driver and hardware.
- Windows speech commands require MSIX package identity, microphone access, and a locally installed speech language pack compatible with the system speech language. The current implementation uses Windows' default microphone because this API does not expose a separate input-device selector.

## Build from source

### Requirements

- Windows 10 1809 or newer, x64.
- Visual Studio 2022 with the Desktop development with C++ workload, MSVC, and a Windows SDK.
- .NET 8 SDK and Windows App SDK 2.5.1 restore access.
- vcpkg available on `PATH`; the repository manifest pins FFmpeg dependencies.
- MSIX packaging tools are needed to create or deploy a packaged app. Running the app unpackaged does not provide the package identity required by some Windows APIs, including Windows speech recognition.

### Build

Run these commands from the repository root in a Developer PowerShell or another shell configured for Visual Studio and vcpkg:

```powershell
Set-Location .\Clippr
vcpkg install --triplet x64-windows --x-manifest-root .
dotnet restore .\Clippr.csproj
dotnet build .\Clippr.csproj -p:Platform=x64 -p:Configuration=Debug
```

The MSBuild target invokes `native\build-native.cmd` to compile the C++23 capture DLL with MSVC and link the vcpkg FFmpeg libraries. The native build expects the Windows App SDK WinUI headers restored by NuGet. The resulting native DLL and FFmpeg runtime DLLs are copied beside the app for packaging.

To run the replay-buffer tests:

```powershell
Set-Location .\Clippr\native
.\test-replay.cmd
```

To verify a saved recording or replay file, build the corresponding helper and provide a path:

```powershell
Set-Location .\Clippr\native
.\verify-recording.cmd C:\path\to\recording.mp4
.\verify-replay.cmd C:\path\to\clip.mkv
```

These checks use Windows Media Foundation for recorded video and FFmpeg for replay container decoding and seeking. A successful compile does not replace a live capture test.

## Install and package

The app uses the MSIX deployment model configured in `Clippr\Package.appxmanifest`. Release scripts and their detailed usage are in [`Clippr/packaging/README.md`](Clippr/packaging/README.md).

For a local unsigned package build:

```powershell
Set-Location .\Clippr
.\packaging\Build-Release.ps1 -Unsigned
```

Unsigned packages are for local validation and are not ready for ordinary public distribution. A distributable package needs a trusted code-signing certificate whose subject matches the package publisher. The current manifest uses the development publisher `CN=ryand`; decide and establish the intended publisher identity before public release. Windows manages the MSIX install directory, Start Menu registration, and Installed Apps entry. User clips and screenshots are stored outside the package directory and are preserved by the uninstall helper.

## Architecture

- **WinUI app:** `Clippr\MainWindow.xaml` and `MainWindow.xaml.cs` implement the capture screen, preview, audio mixer, settings navigation, notifications, tray behavior, and app-level coordination.
- **Settings:** `Clippr\ClipprSettings.cs` owns JSON load, validation, migration, and atomic save. The native settings store is in `Clippr\native\src\settings_store.cpp`.
- **Native capture engine:** `Clippr\native\src\capture.cpp` owns Windows Graphics Capture sessions, Direct3D 11 preview/capture resources, capture selection, and the bounded frame handoff.
- **Encoding and replay:** `Clippr\native\src\hardware_h264_encoder.cpp`, `replay_buffer.cpp`, and `replay_export.cpp` implement hardware encoder selection, compressed packet buffering, and asynchronous MP4/MKV muxing.
- **Audio:** `Clippr\native\src\audio_capture.cpp` implements WASAPI sources and audio packet production. Process-loopback availability is gated by Windows build support.
- **Windows integration:** C# components handle tray commands, notifications, hotkeys, settings UI, and native shell actions. `Clippr\VoiceCommandService.cs` uses a constrained `Windows.Media.SpeechRecognition` grammar.

The C# to native interface is declared in `Clippr\NativeCapture.cs` and `Clippr\native\include\clippr\capture\capture_api.h`.

## Privacy and sharing

Capture, audio processing, replay buffering, and export are local. Voice recognition remains off until the user enables it in Settings > Voice; it uses Windows' on-device list grammar and does not store audio. Microphone access can be denied by Windows privacy settings.

Public direct clip sharing is **not available**. The optional local HTTP component can serve only explicitly selected files over loopback for local use; it does not provide a public URL, router mapping, cloud upload, or relay. The user interface must not describe loopback access as an externally reachable share link.

## Testing and known limitations

The current validation record is in [`Clippr/RELEASE_READINESS_AUDIT.md`](Clippr/RELEASE_READINESS_AUDIT.md) and [`Clippr/PERFORMANCE_NOTES.md`](Clippr/PERFORMANCE_NOTES.md). At the time of that audit:

- x64 Debug and Release builds completed. The replay-buffer test suite passed.
- Two real MKV clips decoded to the end and supported seeking. One exceeded the audit's audio/video end-delta threshold; MP4 export was not verified in that audit.
- The available machine had an AMD GPU. NVIDIA and Intel hardware encoder paths, other Windows releases, and long-session behavior were not verified.
- Live application capture, device-loss recovery, tray and notification interactions, screenshot output, settings restart persistence, and several error paths were not exercised in the audit.
- The new voice-command integration compiled, but real microphone recognition has not yet been verified. Windows microphone permission and a suitable speech language pack are required.
- Public clip sharing, production-trusted signing, and clean-machine install/upgrade/repair validation remain outstanding.

Do not treat the project as production-ready until the outstanding scenarios have been tested on the target Windows versions and hardware.

## Dependencies and licensing

The native engine uses FFmpeg `avcodec`, `avformat`, and `avutil` from the pinned vcpkg manifest. The reviewed build uses shared libraries, disables FFmpeg command-line tools and GPL/nonfree options, and does not enable libx264 or libx265. See [`Clippr/THIRD_PARTY_NOTICES.md`](Clippr/THIRD_PARTY_NOTICES.md) and [`Clippr/FFmpegBuildConfiguration.txt`](Clippr/FFmpegBuildConfiguration.txt) for redistribution obligations and build details. A redistributed build must provide corresponding FFmpeg source and preserve applicable LGPL rights.

No project-wide open-source license has been specified yet. Third-party notices do not grant a license to the ClipDat application source; establish the intended project license before accepting outside contributions or representing the source as open source.

## Repository layout

```text
Clippr.sln                 Visual Studio solution
Clippr/                    WinUI app, native engine, assets, and packaging scripts
Clippr/native/src/          C++23 engine implementation
Clippr/native/include/      Native engine interfaces
Clippr/native/tests/        Replay and media verification helpers
Clippr/packaging/           MSIX build, install, repair, and uninstall scripts
```
