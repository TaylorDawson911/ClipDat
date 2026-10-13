#pragma once

#include "media.hpp"

#include <expected>
#include <stop_token>
#include <string>
#include <vector>

namespace clippr::core {

struct MediaError {
    long code{};
    std::string message;
};

// Implementations must unblock read when the stop token is requested.
class ICaptureSource {
public:
    virtual ~ICaptureSource() = default;
    virtual std::expected<VideoFrame, MediaError> read(std::stop_token stop) = 0;
};

class IAudioSource {
public:
    virtual ~IAudioSource() = default;
    virtual std::expected<AudioFrame, MediaError> read(std::stop_token stop) = 0;
};

// An implementation may use Media Foundation hardware transforms and retain
// the GPU texture until encoding completes.
class IEncoder {
public:
    virtual ~IEncoder() = default;
    virtual std::expected<std::vector<EncodedPacket>, MediaError> encode(const VideoFrame& frame) = 0;
    virtual std::expected<std::vector<EncodedPacket>, MediaError> encode(const AudioFrame& frame) = 0;
    virtual std::expected<std::vector<EncodedPacket>, MediaError> flush() = 0;
};

} // namespace clippr::core
