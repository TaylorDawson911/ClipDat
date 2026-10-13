#pragma once

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <expected>
#include <string>

namespace clippr::core {

enum class CaptureKind { display, application };

struct RecordingSettings {
    CaptureKind source{CaptureKind::display};
    std::uint32_t frames_per_second{60};
    std::chrono::seconds replay_duration{30};
    std::size_t replay_memory_limit_bytes{512ULL * 1024 * 1024};

    [[nodiscard]] std::expected<void, std::string> validate() const;
};

} // namespace clippr::core
