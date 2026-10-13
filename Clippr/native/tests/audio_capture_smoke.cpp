#include "clippr/capture/audio_capture.hpp"

#include <Windows.h>
#include <atomic>
#include <cstdio>
#include <cstdlib>
#include <thread>

int main(int argc, char** argv) {
    const auto outputs = clippr::capture::AudioCaptureManager::Enumerate(true);
    const auto inputs = clippr::capture::AudioCaptureManager::Enumerate(false);
    std::printf("Active output devices: %zu; input devices: %zu\n", outputs.size(), inputs.size());
    for (const auto& device : outputs)
        std::wprintf(L"Output%s: %ls\n", device.is_default ? L" (default)" : L"", device.name.c_str());
    std::atomic<unsigned> packets{};
    std::atomic<std::size_t> bytes{};
    clippr::capture::AudioCaptureManager manager;
    const auto configured = manager.Configure(true, false,
        [&](clippr::core::EncodedPacket&& packet) {
            if (packet.stream == clippr::core::StreamKind::desktop_audio &&
                packet.audio_codec == clippr::core::AudioCodec::aac && !packet.codec_extradata.empty()) {
                ++packets;
                bytes += packet.bytes.size();
            }
        });
    if (FAILED(configured)) {
        std::printf("Audio configure failed: 0x%08lx\n", configured);
        return 1;
    }
    Sleep(2500);
    const auto status = manager.Status();
    manager.Stop();
    std::printf("Desktop loopback running=%d error=0x%08lx AAC packets=%u bytes=%zu\n",
        status.desktop_running, status.desktop_error, packets.load(), bytes.load());
    if (!status.desktop_running || packets.load() == 0) return 2;

    if (argc > 1) {
        const auto process_id = static_cast<uint32_t>(std::strtoul(argv[1], nullptr, 10));
        if (!process_id) return 3;
        packets = 0; bytes = 0;
        const auto process_hr = manager.Configure(false, false,
            [&](clippr::core::EncodedPacket&& packet) {
                if (packet.stream == clippr::core::StreamKind::chat_audio &&
                    packet.audio_codec == clippr::core::AudioCodec::aac && !packet.codec_extradata.empty()) {
                    ++packets;
                    bytes += packet.bytes.size();
                }
            }, {}, {}, 0, process_id);
        if (FAILED(process_hr)) {
            std::printf("Process loopback configure failed: 0x%08lx\n", process_hr);
            return 4;
        }
        Sleep(2500);
        const auto process_status = manager.Status();
        manager.Stop();
        std::printf("Process loopback pid=%lu running=%d error=0x%08lx AAC packets=%u bytes=%zu\n",
            process_id, process_status.chat_running, process_status.chat_error,
            packets.load(), bytes.load());
        if (!process_status.chat_running || packets.load() == 0) return 5;
    }
    return 0;
}
