#pragma once

#include <stdint.h>
#include <wchar.h>

#ifdef __cplusplus
extern "C" {
#endif

struct ClipprDisplay {
    void* monitor;
    wchar_t device_name[32];
    wchar_t friendly_name[128];
    int32_t width;
    int32_t height;
};

struct ClipprWindow {
    void* window;
    uint32_t process_id;
    wchar_t title[256];
    wchar_t application_name[128];
    int32_t minimized;
};

struct ClipprAudioDevice {
    wchar_t id[256];
    wchar_t name[128];
    int32_t is_default;
};
struct ClipprAudioStatus {
    int32_t desktop_running, microphone_running, game_running, chat_running;
    int32_t desktop_error, microphone_error, game_error, chat_error;
    int32_t music_running, music_error;
};
struct ClipprAudioLevels {
    float desktop, microphone, game, chat, music;
};

enum ClipprCaptureState {
    CLIPPR_CAPTURE_STOPPED = 0,
    CLIPPR_CAPTURE_RUNNING = 1,
    CLIPPR_CAPTURE_DISCONNECTED = 2,
    CLIPPR_CAPTURE_ERROR = 3,
    CLIPPR_CAPTURE_UNSUPPORTED = 4,
    CLIPPR_CAPTURE_MINIMIZED = 5
};

struct ClipprCaptureStatus {
    int32_t state;
    int32_t error_code;
    uint64_t frames_presented;
};

struct ClipprEncodingStatus {
    int32_t state; // 0 stopped, 1 starting, 2 encoding, 3 error, 4 finalizing
    int32_t error_code;
    uint64_t frames_encoded;
    uint64_t frames_dropped;
    uint64_t frames_rate_limited;
    int32_t error_stage;
    uint64_t last_write_latency_us;
    uint64_t average_write_latency_us;
    uint64_t max_write_latency_us;
    int32_t active_encoder; // 0 idle, 1 Media Foundation hardware H.264
    uint64_t average_queue_latency_us;
    uint64_t max_queue_latency_us;
};

struct ClipprReplayStatus {
    int32_t state; // 0 stopped, 1 starting, 2 buffering, 3 error
    int32_t error_code;
    uint64_t packets;
    uint64_t ram_bytes;
    uint64_t disk_bytes;
    uint64_t frames_dropped;
    uint64_t saves_completed;
    uint64_t saves_failed;
    int32_t last_save_error;
    int32_t saves_active;
    int32_t last_save_shorter;
    uint64_t frames_encoded;
    uint64_t last_save_duration_ms;
};

// HRESULT-returning functions. The panel pointer is a borrowed WinRT IUnknown.
__declspec(dllexport) long __cdecl ClipprCreateCapture(void* panel, int32_t width, int32_t height, void** capture);
__declspec(dllexport) void __cdecl ClipprDestroyCapture(void* capture);
__declspec(dllexport) long __cdecl ClipprEnumerateDisplays(struct ClipprDisplay* displays, int32_t capacity, int32_t* count);
__declspec(dllexport) long __cdecl ClipprEnumerateWindows(struct ClipprWindow* windows, int32_t capacity, int32_t* count);
// Copies a 32x32 BGRA icon, or returns S_FALSE when no icon is available.
__declspec(dllexport) long __cdecl ClipprCopyWindowIcon(void* window, uint8_t* pixels, int32_t capacity);
__declspec(dllexport) long __cdecl ClipprStartCapture(void* capture, void* monitor);
__declspec(dllexport) long __cdecl ClipprStartWindowCapture(void* capture, void* window);
__declspec(dllexport) void __cdecl ClipprStopCapture(void* capture);
__declspec(dllexport) long __cdecl ClipprSwitchDisplay(void* capture, void* monitor);
__declspec(dllexport) long __cdecl ClipprSwitchWindow(void* capture, void* window);
__declspec(dllexport) long __cdecl ClipprResizePreview(void* capture, int32_t width, int32_t height);
__declspec(dllexport) struct ClipprCaptureStatus __cdecl ClipprGetCaptureStatus(void* capture);
__declspec(dllexport) long __cdecl ClipprStartEncoding(void* capture, const wchar_t* path,
                                                       uint32_t fps, uint32_t bitrate,
                                                       uint32_t width, uint32_t height);
__declspec(dllexport) long __cdecl ClipprStopEncoding(void* capture);
__declspec(dllexport) struct ClipprEncodingStatus __cdecl ClipprGetEncodingStatus(void* capture);
__declspec(dllexport) long __cdecl ClipprConfigureReplay(void* capture, uint32_t duration_seconds,
                                                        uint64_t ram_bytes, uint64_t disk_bytes,
                                                        uint64_t minimum_free_disk_bytes);
__declspec(dllexport) long __cdecl ClipprConfigureReplayOutput(void* capture,
                                                              const wchar_t* folder,
                                                              const wchar_t* extension);
__declspec(dllexport) long __cdecl ClipprSaveReplay(void* capture, uint32_t duration_seconds,
                                                   const wchar_t* path);
__declspec(dllexport) struct ClipprReplayStatus __cdecl ClipprGetReplayStatus(void* capture);
__declspec(dllexport) long __cdecl ClipprGetLastReplayPath(void* capture, wchar_t* path, int32_t capacity);
__declspec(dllexport) long __cdecl ClipprSetReplayEnabled(void* capture, int32_t enabled);
__declspec(dllexport) long __cdecl ClipprEnumerateAudioDevices(int32_t render_devices,
    struct ClipprAudioDevice* devices, int32_t capacity, int32_t* count);
__declspec(dllexport) long __cdecl ClipprConfigureAudio(void* capture, int32_t desktop_enabled,
    int32_t microphone_enabled);
__declspec(dllexport) struct ClipprAudioStatus __cdecl ClipprGetAudioStatus(void* capture);
__declspec(dllexport) struct ClipprAudioLevels __cdecl ClipprTakeAudioLevels(void* capture);
__declspec(dllexport) long __cdecl ClipprSetAudioDevices(void* capture, const wchar_t* render_id,
    const wchar_t* microphone_id);
__declspec(dllexport) long __cdecl ClipprConfigureProcessAudio(void* capture,
    uint32_t game_process_id, uint32_t chat_process_id);
__declspec(dllexport) long __cdecl ClipprConfigureMusicAudio(void* capture, uint32_t music_process_id);
__declspec(dllexport) long __cdecl ClipprSetAudioGain(void* capture, int32_t stream_kind, float gain);
__declspec(dllexport) long __cdecl ClipprConfigureHotkey(void* capture, uint32_t modifiers,
    uint32_t virtual_key);
__declspec(dllexport) long __cdecl ClipprSaveScreenshot(void* capture, const wchar_t* path,
    int32_t jpeg, uint32_t quality);
__declspec(dllexport) long __cdecl ClipprConfigureScreenshot(void* capture, const wchar_t* folder,
    int32_t jpeg, uint32_t quality, uint32_t modifiers, uint32_t virtual_key);
__declspec(dllexport) uint64_t __cdecl ClipprGetScreenshotCount(void* capture);
__declspec(dllexport) int32_t __cdecl ClipprGetScreenshotError(void* capture);
__declspec(dllexport) long __cdecl ClipprGetLastScreenshotPath(void* capture, wchar_t* path, int32_t capacity);

#ifdef __cplusplus
}
#endif
