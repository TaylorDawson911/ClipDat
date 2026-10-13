#pragma once

#include "media.hpp"

#include <chrono>
#include <cstddef>
#include <cstdint>
#include <deque>
#include <expected>
#include <filesystem>
#include <fstream>
#include <memory>
#include <mutex>
#include <string>
#include <vector>

namespace clippr::core {

struct ReplayLimits {
    std::chrono::seconds duration{30};
    std::size_t ram_bytes{128ULL * 1024 * 1024};
    std::size_t disk_bytes{2ULL * 1024 * 1024 * 1024};
    std::filesystem::path disk_directory;
    std::uint64_t minimum_free_disk_bytes{2ULL * 1024 * 1024 * 1024};
};

struct ReplayDiskSegment {
    std::filesystem::path path;
    ~ReplayDiskSegment();
};

struct ReplayPacketRef {
    StreamKind stream{};
    MediaTime timestamp{};
    MediaTime duration{};
    std::optional<MediaTime> decode_timestamp;
    bool keyframe{};
    AudioCodec audio_codec{AudioCodec::unknown};
    std::uint32_t sample_rate{};
    std::uint16_t channels{};
    std::shared_ptr<const std::vector<std::byte>> codec_extradata;
    std::shared_ptr<const std::vector<std::byte>> ram;
    std::shared_ptr<ReplayDiskSegment> disk;
    std::uint64_t disk_offset{};
    std::uint32_t byte_count{};

    [[nodiscard]] std::expected<EncodedPacket, std::string> read() const;
};

// Snapshot copies only packet metadata and storage references while holding the
// buffer lock. Encoded bytes are loaded during export, outside that lock.
struct ReplaySnapshot {
    std::vector<ReplayPacketRef> packets;
    MediaTime start{};
    MediaTime end{};
    bool shorter_than_requested{};
};

class ReplayBuffer final {
public:
    explicit ReplayBuffer(ReplayLimits limits);
    ~ReplayBuffer();
    ReplayBuffer(ReplayBuffer const&) = delete;
    ReplayBuffer& operator=(ReplayBuffer const&) = delete;

    [[nodiscard]] std::expected<void, std::string> push(EncodedPacket packet);
    [[nodiscard]] std::expected<ReplaySnapshot, std::string>
        snapshot(std::chrono::seconds requested_duration) const;
    void clear() noexcept;
    [[nodiscard]] std::size_t packet_count() const noexcept;
    [[nodiscard]] std::size_t ram_bytes() const noexcept;
    [[nodiscard]] std::size_t disk_bytes() const noexcept;

private:
    [[nodiscard]] std::expected<void, std::string> spill(ReplayPacketRef& packet);
    void remove_front() noexcept;
    void trim();
    void close_segment() noexcept;

    ReplayLimits limits_;
    mutable std::mutex mutex_;
    std::deque<ReplayPacketRef> packets_;
    MediaTime latest_{};
    std::size_t ram_bytes_{};
    std::size_t disk_bytes_{};
    std::shared_ptr<ReplayDiskSegment> current_segment_;
    std::ofstream current_stream_;
    std::uint64_t current_size_{};
    std::uint64_t next_segment_{};
};

} // namespace clippr::core
