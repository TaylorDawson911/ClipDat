#pragma once

#include "clippr/core/devices.hpp"

#include <functional>
#include <array>
#include <memory>
#include <string>
#include <vector>

namespace clippr::capture {

struct AudioDeviceInfo {
    std::wstring id;
    std::wstring name;
    bool is_default{};
};

struct AudioCaptureStatus {
    bool desktop_running{};
    bool microphone_running{};
    long desktop_error{};
    long microphone_error{};
    bool game_running{};
    bool chat_running{};
    long game_error{};
    long chat_error{};
    bool music_running{};
    long music_error{};
};

// One WASAPI source per track. The manager owns the workers and joins them on
// reconfiguration/destruction; encoded packets are delivered on worker threads.
class AudioCaptureManager final {
public:
    using PacketCallback = std::function<void(core::EncodedPacket&&)>;
    AudioCaptureManager();
    ~AudioCaptureManager();
    AudioCaptureManager(AudioCaptureManager const&) = delete;
    AudioCaptureManager& operator=(AudioCaptureManager const&) = delete;

    [[nodiscard]] static std::vector<AudioDeviceInfo> Enumerate(bool render_devices);
    long Configure(bool desktop_enabled, bool microphone_enabled,
                   PacketCallback callback, std::wstring render_id = {}, std::wstring microphone_id = {},
                   uint32_t game_process_id = 0, uint32_t chat_process_id = 0,
                   uint32_t music_process_id = 0);
    long SetGain(core::StreamKind kind, float gain) noexcept;
    [[nodiscard]] std::array<float, 5> TakeLevels() noexcept;
    void Stop() noexcept;
    [[nodiscard]] AudioCaptureStatus Status() noexcept;

private:
    struct Impl;
    std::unique_ptr<Impl> impl_;
};

} // namespace clippr::capture
