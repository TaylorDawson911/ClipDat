#include "clippr/core/replay_buffer.hpp"

#include <Windows.h>
#include <algorithm>
#include <limits>
#include <utility>

namespace clippr::core {

ReplayDiskSegment::~ReplayDiskSegment() {
    std::error_code ignored;
    std::filesystem::remove(path, ignored);
}

std::expected<EncodedPacket, std::string> ReplayPacketRef::read() const {
    EncodedPacket result;
    result.stream = stream;
    result.timestamp = timestamp;
    result.duration = duration;
    result.decode_timestamp = decode_timestamp;
    result.keyframe = keyframe;
    result.audio_codec = audio_codec;
    result.sample_rate = sample_rate;
    result.channels = channels;
    if (codec_extradata) result.codec_extradata = *codec_extradata;
    if (ram) {
        result.bytes = *ram;
    } else if (disk) {
        std::ifstream input(disk->path, std::ios::binary);
        if (!input.seekg(static_cast<std::streamoff>(disk_offset)))
            return std::unexpected("Cannot seek replay spool file");
        result.bytes.resize(byte_count);
        if (!input.read(reinterpret_cast<char*>(result.bytes.data()), byte_count))
            return std::unexpected("Cannot read replay spool packet");
    } else {
        return std::unexpected("Replay packet has no storage");
    }
    return result;
}

ReplayBuffer::ReplayBuffer(ReplayLimits limits) : limits_(std::move(limits)) {}
ReplayBuffer::~ReplayBuffer() { clear(); }

void ReplayBuffer::close_segment() noexcept {
    if (current_stream_.is_open()) current_stream_.close();
    current_segment_.reset();
    current_size_ = 0;
}

std::expected<void, std::string> ReplayBuffer::spill(ReplayPacketRef& packet) {
    if (!packet.ram) return {};
    if (limits_.disk_bytes == 0 || limits_.disk_directory.empty())
        return std::unexpected("Disk-backed replay is disabled");
    std::error_code ec;
    std::filesystem::create_directories(limits_.disk_directory, ec);
    if (ec) return std::unexpected("Cannot create replay spool directory");
    ULARGE_INTEGER available{};
    const auto directory = limits_.disk_directory.wstring();
    if (!GetDiskFreeSpaceExW(directory.c_str(), &available, nullptr, nullptr))
        return std::unexpected("Cannot check free space for replay spool");
    if (available.QuadPart < limits_.minimum_free_disk_bytes ||
        packet.byte_count > available.QuadPart - limits_.minimum_free_disk_bytes)
        return std::unexpected("Replay spool stopped to preserve the configured free-space reserve");
    constexpr std::uint64_t segment_limit = 8ULL * 1024 * 1024;
    if (!current_segment_ || current_size_ + packet.byte_count > segment_limit) {
        close_segment();
        auto segment = std::make_shared<ReplayDiskSegment>();
        segment->path = limits_.disk_directory /
            (L"replay-" + std::to_wstring(GetCurrentProcessId()) + L"-" +
             std::to_wstring(reinterpret_cast<std::uintptr_t>(this)) + L"-" +
             std::to_wstring(next_segment_++) + L".bin");
        current_stream_.open(segment->path, std::ios::binary | std::ios::trunc);
        if (!current_stream_) return std::unexpected("Cannot open replay spool segment");
        current_segment_ = std::move(segment);
    }
    auto const& bytes = *packet.ram;
    const auto length = bytes.size();
    current_stream_.write(reinterpret_cast<char const*>(bytes.data()),
                          static_cast<std::streamsize>(length));
    current_stream_.flush(); // A concurrent snapshot may read this segment now.
    if (!current_stream_) return std::unexpected("Cannot write replay spool packet");
    packet.disk = current_segment_;
    packet.disk_offset = current_size_;
    current_size_ += length;
    packet.ram.reset();
    ram_bytes_ -= length;
    disk_bytes_ += length;
    return {};
}

void ReplayBuffer::remove_front() noexcept {
    if (packets_.empty()) return;
    auto const& packet = packets_.front();
    if (packet.ram) ram_bytes_ -= packet.byte_count;
    else if (packet.disk) disk_bytes_ -= packet.byte_count;
    packets_.pop_front();
}

void ReplayBuffer::trim() {
    if (packets_.empty()) return;
    const auto cutoff = latest_ - limits_.duration;
    std::size_t keep_keyframe_index = 0;
    bool found = false;
    for (std::size_t i = 0; i < packets_.size(); ++i) {
        auto const& packet = packets_[i];
        if (packet.stream == StreamKind::video && packet.keyframe && packet.timestamp <= cutoff) {
            keep_keyframe_index = i;
            found = true;
        }
    }
    if (found) for (std::size_t i = 0; i < keep_keyframe_index; ++i) remove_front();

    while (!packets_.empty() &&
           (ram_bytes_ > limits_.ram_bytes || disk_bytes_ > limits_.disk_bytes)) {
        if (ram_bytes_ > limits_.ram_bytes && limits_.disk_bytes > disk_bytes_) {
            auto it = std::find_if(packets_.begin(), packets_.end(),
                                   [](auto const& packet) { return static_cast<bool>(packet.ram); });
            if (it != packets_.end() && it->byte_count <= limits_.disk_bytes - disk_bytes_ &&
                spill(*it)) continue;
        }
        remove_front();
    }
    while (!packets_.empty() &&
           !(packets_.front().stream == StreamKind::video && packets_.front().keyframe))
        remove_front();
    if (packets_.empty()) close_segment();
}

std::expected<void, std::string> ReplayBuffer::push(EncodedPacket packet) {
    if (limits_.duration.count() <= 0 || limits_.ram_bytes == 0 || packet.bytes.empty() ||
        packet.bytes.size() > std::numeric_limits<std::uint32_t>::max())
        return std::unexpected("Invalid replay limits or packet");
    std::scoped_lock lock(mutex_);
    if (packets_.empty() && !(packet.stream == StreamKind::video && packet.keyframe))
        return {}; // No decodable replay exists before the first video keyframe.
    ReplayPacketRef ref;
    ref.stream = packet.stream;
    ref.timestamp = packet.timestamp;
    ref.duration = packet.duration;
    ref.decode_timestamp = packet.decode_timestamp;
    ref.keyframe = packet.keyframe;
    ref.audio_codec = packet.audio_codec;
    ref.sample_rate = packet.sample_rate;
    ref.channels = packet.channels;
    if (!packet.codec_extradata.empty())
        ref.codec_extradata = std::make_shared<const std::vector<std::byte>>(std::move(packet.codec_extradata));
    ref.byte_count = static_cast<std::uint32_t>(packet.bytes.size());
    ref.ram = std::make_shared<const std::vector<std::byte>>(std::move(packet.bytes));
    ram_bytes_ += ref.byte_count;
    latest_ = std::max(latest_, ref.timestamp);
    const auto position = std::upper_bound(packets_.begin(), packets_.end(), ref.timestamp,
        [](MediaTime timestamp, ReplayPacketRef const& existing) { return timestamp < existing.timestamp; });
    packets_.insert(position, std::move(ref));
    trim();
    return {};
}

std::expected<ReplaySnapshot, std::string>
ReplayBuffer::snapshot(std::chrono::seconds requested_duration) const {
    if (requested_duration.count() <= 0)
        return std::unexpected("Replay duration must be positive");
    std::scoped_lock lock(mutex_);
    if (packets_.empty()) return std::unexpected("No encoded replay is available yet");
    const auto target = latest_ - requested_duration;
    std::size_t first_keyframe = packets_.size();
    std::size_t preceding_keyframe = packets_.size();
    for (std::size_t i = 0; i < packets_.size(); ++i) {
        auto const& packet = packets_[i];
        if (packet.stream != StreamKind::video || !packet.keyframe) continue;
        if (first_keyframe == packets_.size()) first_keyframe = i;
        if (packet.timestamp <= target) preceding_keyframe = i;
    }
    if (first_keyframe == packets_.size())
        return std::unexpected("Replay is waiting for a video keyframe");
    const auto start_index = preceding_keyframe == packets_.size()
        ? first_keyframe : preceding_keyframe;
    ReplaySnapshot result;
    result.start = packets_[start_index].timestamp;
    result.end = latest_;
    result.shorter_than_requested = result.start > target;
    result.packets.reserve(packets_.size() - start_index);
    for (std::size_t i = start_index; i < packets_.size(); ++i) {
        if (packets_[i].timestamp >= result.start) result.packets.push_back(packets_[i]);
    }
    return result;
}

void ReplayBuffer::clear() noexcept {
    std::scoped_lock lock(mutex_);
    packets_.clear();
    close_segment();
    latest_ = {};
    ram_bytes_ = 0;
    disk_bytes_ = 0;
}

std::size_t ReplayBuffer::packet_count() const noexcept {
    std::scoped_lock lock(mutex_);
    return packets_.size();
}
std::size_t ReplayBuffer::ram_bytes() const noexcept {
    std::scoped_lock lock(mutex_);
    return ram_bytes_;
}
std::size_t ReplayBuffer::disk_bytes() const noexcept {
    std::scoped_lock lock(mutex_);
    return disk_bytes_;
}

} // namespace clippr::core
