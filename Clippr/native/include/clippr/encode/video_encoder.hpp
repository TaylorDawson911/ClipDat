#pragma once

#include <d3d11.h>
#include <wrl/client.h>
#include <cstdint>
#include <memory>
#include <functional>
#include <string>
#include "clippr/core/media.hpp"

namespace clippr::encode {

struct VideoEncodingConfig {
    uint32_t width{};
    uint32_t height{};
    uint32_t fps{30};
    uint32_t bitrate{8'000'000};
};

// Timestamps are WGC SystemRelativeTime values in 100 ns units. Implementations
// must not retain the caller's frame or block the capture callback indefinitely.
class IVideoEncoder {
public:
    virtual ~IVideoEncoder() = default;
    virtual long Write(ID3D11Texture2D* frame, int64_t timestamp) noexcept = 0;
    virtual long Finish() noexcept = 0;
    virtual wchar_t const* Backend() const noexcept = 0;
    virtual int Stage() const noexcept = 0;
};

// Fails if a compatible hardware H.264 transform cannot be selected.
long CreateHardwareH264Encoder(ID3D11Device* device, VideoEncodingConfig config,
                               std::wstring const& path,
                               std::unique_ptr<IVideoEncoder>& result) noexcept;

using PacketCallback = std::function<void(clippr::core::EncodedPacket&&)>;
long CreateHardwareH264PacketEncoder(ID3D11Device* device, VideoEncodingConfig config,
                                     PacketCallback callback,
                                     std::unique_ptr<IVideoEncoder>& result) noexcept;

} // namespace clippr::encode
