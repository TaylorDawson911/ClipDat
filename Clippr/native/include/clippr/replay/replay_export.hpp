#pragma once

#include "clippr/core/replay_buffer.hpp"
#include "clippr/encode/video_encoder.hpp"

#include <string>

namespace clippr::replay {

// Remuxes encoded packets into MP4 or Matroska without decoding or re-encoding.
// The target is atomically published after the container trailer is written.
long ExportReplayFile(clippr::core::ReplaySnapshot const& snapshot,
                      clippr::encode::VideoEncodingConfig video,
                      std::wstring const& path) noexcept;

} // namespace clippr::replay
