#include "clippr/core/settings.hpp"

namespace clippr::core {

std::expected<void, std::string> RecordingSettings::validate() const {
    if (frames_per_second == 0 || frames_per_second > 240)
        return std::unexpected("Frame rate must be between 1 and 240");
    if (replay_duration.count() <= 0)
        return std::unexpected("Replay duration must be positive");
    if (replay_memory_limit_bytes == 0)
        return std::unexpected("Replay memory limit must be positive");
    return {};
}

} // namespace clippr::core
