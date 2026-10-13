#include "clippr/core/replay_buffer.hpp"

#include <atomic>
#include <algorithm>
#include <chrono>
#include <filesystem>
#include <stdexcept>
#include <thread>
#include <vector>
#include <cstdio>

using namespace std::chrono_literals;
using clippr::core::EncodedPacket;
using clippr::core::MediaTime;
using clippr::core::ReplayBuffer;
using clippr::core::ReplayLimits;
using clippr::core::StreamKind;

void Check(bool condition, char const* message) {
    if (!condition) throw std::runtime_error(message);
}

EncodedPacket Packet(int64_t seconds, bool keyframe, std::size_t bytes = 80,
                     StreamKind stream = StreamKind::video) {
    EncodedPacket packet;
    packet.stream = stream;
    packet.timestamp = MediaTime{seconds * 10'000'000};
    packet.duration = MediaTime{333'333};
    packet.decode_timestamp = packet.timestamp;
    packet.keyframe = keyframe;
    packet.bytes.assign(bytes, std::byte{static_cast<unsigned char>(seconds & 0xff)});
    return packet;
}

void TestWraparoundAndKeyframes() {
    ReplayBuffer buffer({10s, 1'000'000, 0, {}});
    for (int seconds = 0; seconds <= 23; ++seconds)
        Check(buffer.push(Packet(seconds, seconds % 5 == 0)).has_value(), "push failed");
    auto snapshot = buffer.snapshot(10s);
    Check(snapshot.has_value(), "snapshot failed");
    Check(snapshot->start == MediaTime{100'000'000}, "did not start at prior keyframe");
    Check(snapshot->end == MediaTime{230'000'000}, "wrong end timestamp");
    Check(snapshot->packets.front().keyframe, "snapshot not keyframe aligned");
    Check(!snapshot->shorter_than_requested, "incorrect short snapshot flag");
    auto short_clip = buffer.snapshot(60s);
    Check(short_clip && short_clip->shorter_than_requested, "unavailable duration not reported");
}

void TestMemoryLimitAndDisk() {
    const auto folder = std::filesystem::temp_directory_path() / L"clippr-replay-test";
    std::filesystem::create_directories(folder);
    ReplayBuffer buffer({60s, 100, 1'000, folder});
    Check(buffer.push(Packet(0, true)).has_value(), "first disk push failed");
    Check(buffer.push(Packet(1, false)).has_value(), "second disk push failed");
    Check(buffer.push(Packet(2, false)).has_value(), "third disk push failed");
    Check(buffer.ram_bytes() <= 100, "RAM limit exceeded");
    Check(buffer.disk_bytes() >= 160, "old packets not spilled to disk");
    auto snapshot = buffer.snapshot(30s);
    Check(snapshot && snapshot->packets.size() == 3, "disk snapshot incomplete");
    for (auto const& ref : snapshot->packets) {
        auto packet = ref.read();
        Check(packet && packet->bytes.size() == 80, "spooled packet unreadable");
        Check(packet->bytes.front() == std::byte{static_cast<unsigned char>(
                  packet->timestamp.count() / 10'000'000)}, "spooled packet corrupted");
    }
    buffer.clear();
    Check(snapshot->packets.front().read().has_value(), "snapshot invalidated by clear");
    snapshot = std::unexpected("released");
    std::error_code ignored;
    std::filesystem::remove(folder, ignored);
}

void TestFreeSpaceReserveBlocksSpill() {
    const auto folder = std::filesystem::temp_directory_path() / L"clippr-replay-reserve-test";
    ReplayBuffer buffer({60s, 100, 1'000, folder, UINT64_MAX});
    Check(buffer.push(Packet(0, true)).has_value(), "reserve keyframe push failed");
    Check(buffer.push(Packet(1, false)).has_value(), "reserve interframe push failed");
    Check(buffer.push(Packet(2, false)).has_value(), "reserve overflow push failed");
    Check(buffer.disk_bytes() == 0, "replay spilled below configured free-space reserve");
    Check(buffer.ram_bytes() <= 100, "free-space protection broke the RAM bound");
    Check(buffer.snapshot(1s).has_value() == false, "expected bounded replay to wait for a decodable keyframe");
    std::error_code ignored;
    std::filesystem::remove_all(folder, ignored);
}

void TestHardMemoryLimitAndAudioTimestamps() {
    ReplayBuffer buffer({30s, 200, 0, {}});
    Check(buffer.push(Packet(0, true)).has_value(), "keyframe push failed");
    Check(buffer.push(Packet(0, false, 40, StreamKind::audio)).has_value(), "audio push failed");
    Check(buffer.push(Packet(1, false)).has_value(), "interframe push failed");
    Check(buffer.push(Packet(2, true)).has_value(), "second keyframe push failed");
    Check(buffer.ram_bytes() <= 200, "hard memory cap exceeded");
    auto snapshot = buffer.snapshot(30s);
    Check(snapshot && snapshot->packets.front().keyframe, "hard cap lost keyframe alignment");
    for (auto const& ref : snapshot->packets) {
        auto packet = ref.read();
        Check(packet && packet->timestamp == ref.timestamp &&
              packet->duration == ref.duration &&
              packet->decode_timestamp == ref.decode_timestamp,
              "packet timing changed");
    }
    ReplayBuffer av({30s, 1'000, 0, {}});
    Check(av.push(Packet(0, true)).has_value(), "AV keyframe push failed");
    auto audio = Packet(0, false, 32, StreamKind::audio);
    audio.timestamp = MediaTime{1'000'000};
    audio.audio_codec = clippr::core::AudioCodec::aac;
    audio.sample_rate = 48'000;
    audio.channels = 2;
    audio.codec_extradata = {std::byte{0x11}, std::byte{0x90}};
    Check(av.push(std::move(audio)).has_value(), "AV audio push failed");
    auto clip = av.snapshot(10s);
    Check(clip && clip->packets.size() == 2 &&
          clip->packets[1].stream == StreamKind::audio &&
          clip->packets[1].timestamp == MediaTime{1'000'000}, "audio synchronization lost");
    auto saved_audio = clip->packets[1].read();
    Check(saved_audio && saved_audio->audio_codec == clippr::core::AudioCodec::aac &&
          saved_audio->sample_rate == 48'000 && saved_audio->channels == 2 &&
          saved_audio->codec_extradata.size() == 2, "audio stream metadata lost");
}

void TestIndependentAudioTracksRemainTimestamped() {
    ReplayBuffer buffer({30s, 1'000'000, 0, {}});
    auto video = Packet(0, true, 64);
    Check(buffer.push(std::move(video)).has_value(), "video keyframe push failed");
    auto mic = Packet(2, false, 20, StreamKind::microphone_audio);
    mic.audio_codec = clippr::core::AudioCodec::aac;
    mic.decode_timestamp = MediaTime{19'950'000};
    auto desktop = Packet(1, false, 20, StreamKind::desktop_audio);
    desktop.audio_codec = clippr::core::AudioCodec::aac;
    Check(buffer.push(std::move(mic)).has_value(), "microphone push failed");
    Check(buffer.push(std::move(desktop)).has_value(), "desktop audio push failed");
    auto snapshot = buffer.snapshot(30s);
    Check(snapshot.has_value(), "independent audio snapshot failed");
    const auto mic_ref = std::find_if(snapshot->packets.begin(), snapshot->packets.end(),
        [](auto const& packet) { return packet.stream == StreamKind::microphone_audio; });
    const auto desktop_ref = std::find_if(snapshot->packets.begin(), snapshot->packets.end(),
        [](auto const& packet) { return packet.stream == StreamKind::desktop_audio; });
    Check(mic_ref != snapshot->packets.end() && desktop_ref != snapshot->packets.end(), "audio tracks merged or lost");
    Check(mic_ref->timestamp == MediaTime{20'000'000} && mic_ref->decode_timestamp == MediaTime{19'950'000},
          "microphone timestamps changed");
    Check(desktop_ref->timestamp == 1s, "desktop track timestamp changed");
}

void TestConcurrentSaves() {
    ReplayBuffer buffer({20s, 3'000, 40'000,
        std::filesystem::temp_directory_path() / L"clippr-replay-concurrent-test"});
    std::atomic<bool> failed{false};
    std::thread producer([&] {
        for (int i = 0; i < 2'000; ++i) {
            auto packet = Packet(i / 30, i % 30 == 0, 80);
            packet.timestamp = MediaTime{static_cast<int64_t>(i) * 333'333};
            if (!buffer.push(std::move(packet))) failed = true;
        }
    });
    auto saver = [&] {
        for (int i = 0; i < 200; ++i) {
            auto snapshot = buffer.snapshot(10s);
            if (!snapshot) continue;
            if (!snapshot->packets.front().keyframe) failed = true;
            for (auto const& ref : snapshot->packets) {
                auto packet = ref.read();
                if (!packet || packet->bytes.size() != 80) failed = true;
            }
        }
    };
    std::thread first(saver), second(saver);
    producer.join(); first.join(); second.join();
    Check(!failed, "concurrent snapshot corrupted packet data");
    Check(buffer.ram_bytes() <= 3'000, "concurrent RAM limit exceeded");
    Check(buffer.disk_bytes() <= 40'000, "concurrent disk limit exceeded");
}

int main() {
    try {
        TestWraparoundAndKeyframes();
        TestMemoryLimitAndDisk();
        TestFreeSpaceReserveBlocksSpill();
        TestHardMemoryLimitAndAudioTimestamps();
        TestIndependentAudioTracksRemainTimestamped();
        TestConcurrentSaves();
        std::puts("Replay buffer tests passed");
        return 0;
    } catch (std::exception const& ex) {
        std::printf("Replay buffer test failed: %s\n", ex.what());
        return 1;
    }
}
