#pragma once

#include <d3d11.h>
#include <wrl/client.h>

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <optional>
#include <vector>

namespace clippr::core {

// Media Foundation and WGC timestamps use 100 ns units.
using MediaTime = std::chrono::duration<std::int64_t, std::ratio<1, 10'000'000>>;

// The texture remains owned while an encoder is consuming this frame.
struct VideoFrame {
    Microsoft::WRL::ComPtr<ID3D11Texture2D> texture;
    MediaTime timestamp{};
    std::uint32_t width{};
    std::uint32_t height{};
};

struct AudioFrame {
    std::vector<float> interleaved_samples;
    MediaTime timestamp{};
    std::uint32_t sample_rate{};
    std::uint16_t channels{};
};

enum class StreamKind {
    video, desktop_audio, microphone_audio, game_audio, chat_audio, music_audio,
    audio = microphone_audio
};
enum class AudioCodec { unknown, aac, opus };

struct EncodedPacket {
    StreamKind stream{};
    MediaTime timestamp{};
    MediaTime duration{};
    std::optional<MediaTime> decode_timestamp;
    bool keyframe{};
    AudioCodec audio_codec{AudioCodec::unknown};
    std::uint32_t sample_rate{};
    std::uint16_t channels{};
    std::vector<std::byte> codec_extradata;
    std::vector<std::byte> bytes;
};

} // namespace clippr::core
