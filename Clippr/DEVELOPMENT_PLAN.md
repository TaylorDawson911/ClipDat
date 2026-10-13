# Clippr development plan

The checked-in application is C#/.NET 8 WinUI 3, not C++/WinRT. Preserve its existing XAML and use a separate C++23 native core rather than replacing the window.

1. **Shared core (this change):** Define GPU video frames, audio frames, encoded packets, cancellable capture/audio interfaces, encoder interface, validated recording settings, and a bounded keyframe-aware replay buffer. Build it independently with MSVC. No recording is exposed to the UI yet.
2. **Capture and preview (screen capture implemented):** Enumerate monitors, capture the selected monitor with Windows Graphics Capture, and present its real D3D11 frames through a WinUI `SwapChainPanel`. Screen capture can start, stop, and switch displays without restarting. Window/application capture remains separate work. Device loss, resolution changes, and source closure have recovery paths, but only normal capture, stop/restart, and resizing have been exercised on the available single-monitor machine.
3. **Audio and encoding:** Capture system/microphone audio with WASAPI, encode with Media Foundation hardware transforms when available, and provide a documented software fallback.
4. **Recording and clipping:** Add a cancellable worker pipeline, mux encoded audio/video into a real file, save replay snapshots from a keyframe, and report errors to WinUI. Connect the existing source/display controls and add recording actions without replacing their design.
5. **Verification:** Build the native core and WinUI project; exercise real capture, audio, recording, replay save, cancellation, and cleanup on the running app.

Stage 2 adds `native/include/clippr/capture/capture_api.h`, `native/src/capture.cpp`, `native/build-native.cmd`, and `NativeCapture.cs`. It updates `Clippr.csproj`, `MainWindow.xaml`, and `MainWindow.xaml.cs` to build and use the native capture DLL while keeping the existing toolbar layout.
