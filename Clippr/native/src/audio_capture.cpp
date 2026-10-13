#include "clippr/capture/audio_capture.hpp"

#include <Windows.h>
#include <winternl.h>
#include <audioclient.h>
#include <audioclientactivationparams.h>
#include <mmdeviceapi.h>
#include <ksmedia.h>
#include <wrl/implements.h>
#include <functiondiscoverykeys_devpkey.h>
#include <wrl/client.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <libavutil/frame.h>
#include <libavutil/mem.h>
#include <libavutil/mathematics.h>
}

#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstring>
#include <mutex>
#include <thread>
#include <chrono>

using Microsoft::WRL::ComPtr;
namespace clippr::capture {
namespace {

std::wstring DeviceName(IMMDevice* device) {
    ComPtr<IPropertyStore> properties;
    if (FAILED(device->OpenPropertyStore(STGM_READ, properties.GetAddressOf()))) return L"Audio device";
    PROPVARIANT value{}; PropVariantInit(&value);
    std::wstring name = L"Audio device";
    if (SUCCEEDED(properties->GetValue(PKEY_Device_FriendlyName, &value)) && value.vt == VT_LPWSTR && value.pwszVal)
        name = value.pwszVal;
    PropVariantClear(&value);
    return name;
}

struct AacEncoder {
    AVCodecContext* context{};
    AVFrame* frame{};
    AVPacket* packet{};
    int64_t next_pts{};
    std::vector<float> samples;
    std::size_t consumed{};
    uint32_t rate{48000};
    uint16_t channels{2};
    core::StreamKind stream{};
    AudioCaptureManager::PacketCallback callback;
    std::atomic<long>* error{};

    ~AacEncoder() { av_packet_free(&packet); av_frame_free(&frame); avcodec_free_context(&context); }
    bool Init(core::StreamKind kind, AudioCaptureManager::PacketCallback cb, std::atomic<long>* err) {
        stream = kind; callback = std::move(cb); error = err;
        auto* codec = avcodec_find_encoder(AV_CODEC_ID_AAC);
        if (!codec) { *error = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED); return false; }
        context = avcodec_alloc_context3(codec);
        if (!context) { *error = E_OUTOFMEMORY; return false; }
        context->sample_rate = rate; context->sample_fmt = AV_SAMPLE_FMT_FLTP;
        context->bit_rate = 128000; context->time_base = AVRational{1, static_cast<int>(rate)};
        av_channel_layout_default(&context->ch_layout, channels);
        int r = avcodec_open2(context, codec, nullptr);
        if (r < 0) { *error = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED); return false; }
        frame = av_frame_alloc(); packet = av_packet_alloc();
        if (!frame || !packet) { *error = E_OUTOFMEMORY; return false; }
        frame->format = context->sample_fmt; frame->sample_rate = context->sample_rate;
        frame->nb_samples = context->frame_size ? context->frame_size : 1024;
        av_channel_layout_copy(&frame->ch_layout, &context->ch_layout);
        if (av_frame_get_buffer(frame, 0) < 0) { *error = E_OUTOFMEMORY; return false; }
        return true;
    }
    bool EncodeAvailable(bool flush = false) {
        if (!context || !frame || !packet) return false;
        const auto frame_size = static_cast<std::size_t>(frame->nb_samples);
        while (samples.size() - consumed >= frame_size * channels || (flush && samples.size() > consumed)) {
            if (av_frame_make_writable(frame) < 0) { *error = E_FAIL; return false; }
            const auto available = std::min(frame_size, (samples.size() - consumed) / channels);
            const int64_t pts = next_pts;
            for (std::size_t i = 0; i < frame_size; ++i) for (std::size_t c = 0; c < channels; ++c) {
                float value = i < available ? samples[consumed + i * channels + c] : 0.f;
                reinterpret_cast<float*>(frame->data[c])[i] = value;
            }
            frame->pts = pts; next_pts += frame_size;
            consumed += available * channels;
            int r = avcodec_send_frame(context, frame);
            if (r < 0) { *error = E_FAIL; return false; }
            while ((r = avcodec_receive_packet(context, packet)) >= 0) {
                core::EncodedPacket out;
                out.stream = stream; out.audio_codec = core::AudioCodec::aac;
                out.sample_rate = rate; out.channels = channels;
                out.timestamp = core::MediaTime{av_rescale_q(packet->pts, context->time_base, AVRational{1, 10'000'000})};
                out.decode_timestamp = core::MediaTime{av_rescale_q(packet->dts, context->time_base, AVRational{1, 10'000'000})};
                out.duration = core::MediaTime{av_rescale_q(packet->duration, context->time_base, AVRational{1, 10'000'000})};
                if (context->extradata && context->extradata_size > 0) {
                    out.codec_extradata.resize(context->extradata_size);
                    std::memcpy(out.codec_extradata.data(), context->extradata, context->extradata_size);
                }
                out.bytes.resize(packet->size); std::memcpy(out.bytes.data(), packet->data, packet->size);
                callback(std::move(out)); av_packet_unref(packet);
            }
            if (r != AVERROR(EAGAIN) && r != AVERROR_EOF) { *error = E_FAIL; return false; }
        }
        if (consumed) { samples.erase(samples.begin(), samples.begin() + static_cast<std::ptrdiff_t>(consumed)); consumed = 0; }
        return true;
    }
    bool Flush() {
        if (!EncodeAvailable(true)) return false;
        int r = avcodec_send_frame(context, nullptr);
        if (r < 0 && r != AVERROR_EOF) { *error = E_FAIL; return false; }
        while ((r = avcodec_receive_packet(context, packet)) >= 0) {
            core::EncodedPacket out;
            out.stream = stream; out.audio_codec = core::AudioCodec::aac;
            out.sample_rate = rate; out.channels = channels;
            out.timestamp = core::MediaTime{av_rescale_q(packet->pts, context->time_base, AVRational{1, 10'000'000})};
            out.decode_timestamp = core::MediaTime{av_rescale_q(packet->dts, context->time_base, AVRational{1, 10'000'000})};
            out.duration = core::MediaTime{av_rescale_q(packet->duration, context->time_base, AVRational{1, 10'000'000})};
            if (context->extradata && context->extradata_size > 0) {
                out.codec_extradata.resize(context->extradata_size);
                std::memcpy(out.codec_extradata.data(), context->extradata, context->extradata_size);
            }
            out.bytes.resize(packet->size); std::memcpy(out.bytes.data(), packet->data, packet->size);
            callback(std::move(out)); av_packet_unref(packet);
        }
        if (r != AVERROR_EOF) { *error = E_FAIL; return false; }
        return true;
    }
};

class ProcessLoopbackActivation final : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>,
    Microsoft::WRL::FtmBase, IActivateAudioInterfaceCompletionHandler> {
public:
    explicit ProcessLoopbackActivation(HANDLE ready) : ready_(ready) {}
    STDMETHODIMP ActivateCompleted(IActivateAudioInterfaceAsyncOperation* operation) override {
        ComPtr<IUnknown> activated;
        HRESULT activation{};
        result_ = operation->GetActivateResult(&activation, activated.GetAddressOf());
        if (SUCCEEDED(result_)) result_ = activation;
        if (SUCCEEDED(result_)) audio_ = std::move(activated);
        SetEvent(ready_);
        return S_OK;
    }
    HRESULT result() const noexcept { return result_; }
    IUnknown* audio() const noexcept { return audio_.Get(); }
private:
    HANDLE ready_{};
    HRESULT result_{E_UNEXPECTED};
    ComPtr<IUnknown> audio_;
};

HRESULT ActivateProcessLoopback(DWORD process_id, HANDLE ready, IAudioClient** client) {
    if (!process_id || !ready || !client) return E_INVALIDARG;
    AUDIOCLIENT_ACTIVATION_PARAMS params{};
    params.ActivationType = AUDIOCLIENT_ACTIVATION_TYPE_PROCESS_LOOPBACK;
    params.ProcessLoopbackParams.TargetProcessId = process_id;
    params.ProcessLoopbackParams.ProcessLoopbackMode = PROCESS_LOOPBACK_MODE_INCLUDE_TARGET_PROCESS_TREE;
    PROPVARIANT activation_params{};
    activation_params.vt = VT_BLOB;
    activation_params.blob.cbSize = sizeof(params);
    activation_params.blob.pBlobData = reinterpret_cast<BYTE*>(&params);
    auto completion = Microsoft::WRL::Make<ProcessLoopbackActivation>(ready);
    if (!completion) return E_OUTOFMEMORY;
    ComPtr<IActivateAudioInterfaceAsyncOperation> operation;
    HRESULT hr = ActivateAudioInterfaceAsync(VIRTUAL_AUDIO_DEVICE_PROCESS_LOOPBACK, __uuidof(IAudioClient),
        &activation_params, completion.Get(), operation.GetAddressOf());
    if (FAILED(hr)) return hr;
    const DWORD wait = WaitForSingleObject(ready, 10'000);
    if (wait != WAIT_OBJECT_0) return wait == WAIT_TIMEOUT ? HRESULT_FROM_WIN32(ERROR_TIMEOUT) : HRESULT_FROM_WIN32(GetLastError());
    if (FAILED(completion->result())) return completion->result();
    ComPtr<IAudioClient> activated;
    hr = completion->audio()->QueryInterface(IID_PPV_ARGS(activated.GetAddressOf()));
    if (SUCCEEDED(hr)) *client = activated.Detach();
    return hr;
}

float ReadSample(BYTE const* p, WAVEFORMATEX const* format) noexcept {
    if (format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT ||
        (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE && reinterpret_cast<WAVEFORMATEXTENSIBLE const*>(format)->SubFormat == KSDATAFORMAT_SUBTYPE_IEEE_FLOAT)) {
        float f{}; std::memcpy(&f, p, sizeof(f)); return std::isfinite(f) ? std::clamp(f, -1.f, 1.f) : 0.f;
    }
    const WORD bits = format->wFormatTag == WAVE_FORMAT_EXTENSIBLE
        ? reinterpret_cast<WAVEFORMATEXTENSIBLE const*>(format)->Samples.wValidBitsPerSample : format->wBitsPerSample;
    if (bits == 8) return (static_cast<int>(*p) - 128) / 128.f;
    if (bits == 16) { int16_t v{}; std::memcpy(&v,p,2); return v / 32768.f; }
    if (bits == 24) { int32_t v = p[0] | (p[1] << 8) | (p[2] << 16); if (v & 0x800000) v |= ~0xffffff; return v / 8388608.f; }
    if (bits == 32) { int32_t v{}; std::memcpy(&v,p,4); return static_cast<float>(v / 2147483648.0); }
    return 0.f;
}

struct WasapiWorker {
    std::wstring id; bool loopback{}; DWORD process_id{}; core::StreamKind kind{};
    std::atomic<float>* gain{};
    std::atomic<float>* meter{};
    AudioCaptureManager::PacketCallback callback;
    std::thread thread; HANDLE stop_event{CreateEventW(nullptr, TRUE, FALSE, nullptr)};
    std::atomic<bool> running{}; std::atomic<long> error{};
    ~WasapiWorker() { Stop(); if (stop_event) CloseHandle(stop_event); }
    void Start() { thread = std::thread([this] { Run(); }); }
    void Stop() noexcept { if (stop_event) SetEvent(stop_event); if (thread.joinable()) thread.join(); running = false; }
    void Run() noexcept {
        const HRESULT ci = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(ci)) { error = ci; return; }
        ComPtr<IMMDeviceEnumerator> enumerator;
        ComPtr<IMMDevice> device;
        ComPtr<IAudioClient> client;
        HRESULT hr = S_OK;
        HANDLE activation_event = process_id ? CreateEventW(nullptr, TRUE, FALSE, nullptr) : nullptr;
        if (process_id && !activation_event) hr = HRESULT_FROM_WIN32(GetLastError());
        if (process_id && SUCCEEDED(hr)) {
            RTL_OSVERSIONINFOW version{}; version.dwOSVersionInfoSize = sizeof(version);
            using RtlGetVersionFn = LONG (WINAPI*)(PRTL_OSVERSIONINFOW);
            const auto rtl_get_version = reinterpret_cast<RtlGetVersionFn>(GetProcAddress(
                GetModuleHandleW(L"ntdll.dll"), "RtlGetVersion"));
            if (!rtl_get_version || rtl_get_version(&version) != 0 || version.dwBuildNumber < 20348)
                hr = HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
        }
        if (process_id && SUCCEEDED(hr)) hr = ActivateProcessLoopback(process_id, activation_event, client.GetAddressOf());
        if (!process_id) {
            hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  IID_PPV_ARGS(enumerator.GetAddressOf()));
            if (SUCCEEDED(hr)) hr = id.empty()
                ? enumerator->GetDefaultAudioEndpoint(loopback ? eRender : eCapture, eConsole, device.GetAddressOf())
                : enumerator->GetDevice(id.c_str(), device.GetAddressOf());
            if (SUCCEEDED(hr)) hr = device->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(client.GetAddressOf()));
        }
        WAVEFORMATEX process_loopback_format{
            WAVE_FORMAT_PCM, 2, 44'100, 176'400, 4, 16, 0};
        WAVEFORMATEX* format{};
        if (SUCCEEDED(hr)) {
            if (process_id) {
                // The process loopback virtual endpoint returns E_NOTIMPL from
                // GetMixFormat on supported Windows builds. Microsoft’s sample
                // initializes it with shared-mode 16-bit stereo PCM instead.
                format = &process_loopback_format;
            } else {
                hr = client->GetMixFormat(&format);
            }
        }
        HANDLE audio_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (SUCCEEDED(hr) && !audio_event) hr = HRESULT_FROM_WIN32(GetLastError());
        DWORD flags = AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
        if (loopback || process_id) flags |= AUDCLNT_STREAMFLAGS_LOOPBACK;
        if (process_id) flags |= AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
        if (SUCCEEDED(hr)) hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 0, 0, format, nullptr);
        if (SUCCEEDED(hr)) hr = client->SetEventHandle(audio_event);
        ComPtr<IAudioCaptureClient> capture;
        if (SUCCEEDED(hr)) hr = client->GetService(IID_PPV_ARGS(capture.GetAddressOf()));
        AacEncoder encoder;
        if (SUCCEEDED(hr) && !encoder.Init(kind, callback, &error)) hr = static_cast<HRESULT>(error.load());
        if (SUCCEEDED(hr)) hr = client->Start();
        if (FAILED(hr)) error = hr;
        else running = true;
        uint64_t remainder{};
        while (SUCCEEDED(hr) && WaitForSingleObject(stop_event, 0) != WAIT_OBJECT_0) {
            HANDLE handles[]{stop_event, audio_event};
            const DWORD wait = WaitForMultipleObjects(2, handles, FALSE, 250);
            if (wait == WAIT_OBJECT_0) break;
            if (wait == WAIT_TIMEOUT) continue;
            if (wait != WAIT_OBJECT_0 + 1) { error = HRESULT_FROM_WIN32(GetLastError()); break; }
            UINT32 packet_frames{};
            if (FAILED(hr = capture->GetNextPacketSize(&packet_frames))) break;
            while (packet_frames) {
                BYTE* data{}; UINT32 frames{}; DWORD buffer_flags{}; UINT64 device_pos{}, qpc_pos{};
                if (FAILED(hr = capture->GetBuffer(&data, &frames, &buffer_flags, &device_pos, &qpc_pos))) break;
                if (frames && format->nChannels && format->nSamplesPerSec) {
                    const uint32_t in_rate = format->nSamplesPerSec;
                    const uint32_t out_count = static_cast<uint32_t>((static_cast<uint64_t>(frames) * 48000 + remainder) / in_rate);
                    remainder = (static_cast<uint64_t>(frames) * 48000 + remainder) % in_rate;
                    std::vector<float> converted(static_cast<std::size_t>(out_count) * 2);
                    const auto block = format->nBlockAlign;
                    const float level = gain ? gain->load(std::memory_order_relaxed) : 1.f;
                    float block_peak = 0.f;
                    for (uint32_t i = 0; i < out_count; ++i) {
                        const double position = (static_cast<double>(i) * in_rate) / 48000.0;
                        const auto src = std::min<uint32_t>(static_cast<uint32_t>(position), frames - 1);
                        const auto src_next = std::min<uint32_t>(src + 1, frames - 1);
                        const float fraction = static_cast<float>(position - src);
                        for (uint16_t ch = 0; ch < 2; ++ch) {
                            const auto source_ch = std::min<uint16_t>(ch, format->nChannels - 1);
                            const auto bytes_per_sample = format->wBitsPerSample / 8;
                            float value = 0.f;
                            if (!(buffer_flags & AUDCLNT_BUFFERFLAGS_SILENT)) {
                                const float a = ReadSample(data + src * block + source_ch * bytes_per_sample, format);
                                const float b = ReadSample(data + src_next * block + source_ch * bytes_per_sample, format);
                                value = a + (b - a) * fraction;
                            }
                            if (format->nChannels > 1 && ch == 0 && format->nChannels > 2) {
                                for (uint16_t c = 2; c < format->nChannels; ++c)
                                    if (!(buffer_flags & AUDCLNT_BUFFERFLAGS_SILENT)) {
                                        const float a = ReadSample(data + src * block + c * bytes_per_sample, format);
                                        const float b = ReadSample(data + src_next * block + c * bytes_per_sample, format);
                                        value += .35f * (a + (b - a) * fraction);
                                    }
                                value = std::clamp(value, -1.f, 1.f);
                            }
                            const float sample = std::clamp(value * level, -1.f, 1.f);
                            converted[static_cast<std::size_t>(i) * 2 + ch] = sample;
                            block_peak = std::max(block_peak, std::abs(sample));
                        }
                    }
                    if (meter) {
                        float previous = meter->load(std::memory_order_relaxed);
                        while (previous < block_peak && !meter->compare_exchange_weak(
                            previous, block_peak, std::memory_order_relaxed)) {}
                    }
                    encoder.samples.insert(encoder.samples.end(), converted.begin(), converted.end());
                    if (qpc_pos && encoder.next_pts == 0) encoder.next_pts = av_rescale_q(static_cast<int64_t>(qpc_pos), AVRational{1, 10'000'000}, AVRational{1, 48000});
                    if (encoder.next_pts == 0) { LARGE_INTEGER now{}; QueryPerformanceCounter(&now); LARGE_INTEGER freq{}; QueryPerformanceFrequency(&freq); encoder.next_pts = av_rescale(now.QuadPart, 48000LL * 10'000'000LL, freq.QuadPart); }
                    if (!encoder.EncodeAvailable()) { hr = E_FAIL; break; }
                }
                capture->ReleaseBuffer(frames);
                if (FAILED(hr = capture->GetNextPacketSize(&packet_frames))) break;
            }
        }
        if (FAILED(hr) && error.load() == 0) error = hr;
        if (client) (void)client->Stop();
        if (format && !process_id) CoTaskMemFree(format);
        if (activation_event) CloseHandle(activation_event);
        if (audio_event) CloseHandle(audio_event);
        (void)encoder.Flush();
        running = false;
        CoUninitialize();
    }
};
} // namespace

struct AudioCaptureManager::Impl {
    mutable std::mutex mutex;
    std::unique_ptr<WasapiWorker> desktop, microphone, game, chat, music;
    std::array<std::atomic<float>, 5> gains{};
    std::array<std::atomic<float>, 5> meters{};
    PacketCallback callback;
    bool want_desktop{}, want_microphone{};
    DWORD game_pid{}, chat_pid{}, music_pid{};
    std::wstring render_id, microphone_id;
    std::chrono::steady_clock::time_point retry_desktop{}, retry_microphone{};
    std::chrono::steady_clock::time_point retry_game{}, retry_chat{}, retry_music{};
};

AudioCaptureManager::AudioCaptureManager() : impl_(std::make_unique<Impl>()) {
    for (auto& gain : impl_->gains) gain.store(1.f);
    for (auto& meter : impl_->meters) meter.store(0.f);
}
AudioCaptureManager::~AudioCaptureManager() { Stop(); }

std::vector<AudioDeviceInfo> AudioCaptureManager::Enumerate(bool render_devices) {
    std::vector<AudioDeviceInfo> result;
    const HRESULT co = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    const bool uninit = SUCCEEDED(co);
    ComPtr<IMMDeviceEnumerator> enumerator;
    ComPtr<IMMDeviceCollection> collection;
    if (SUCCEEDED(CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(enumerator.GetAddressOf()))) &&
        SUCCEEDED(enumerator->EnumAudioEndpoints(render_devices ? eRender : eCapture, DEVICE_STATE_ACTIVE, collection.GetAddressOf()))) {
        UINT count{}; collection->GetCount(&count);
        ComPtr<IMMDevice> default_device;
        (void)enumerator->GetDefaultAudioEndpoint(render_devices ? eRender : eCapture, eConsole, default_device.GetAddressOf());
        LPWSTR default_id{}; if (default_device) (void)default_device->GetId(&default_id);
        for (UINT i = 0; i < count; ++i) {
            ComPtr<IMMDevice> device; if (FAILED(collection->Item(i, device.GetAddressOf()))) continue;
            LPWSTR id{}; if (FAILED(device->GetId(&id))) continue;
            result.push_back({id, DeviceName(device.Get()), default_id && _wcsicmp(id, default_id) == 0});
            CoTaskMemFree(id);
        }
        if (default_id) CoTaskMemFree(default_id);
    }
    if (uninit) CoUninitialize();
    return result;
}

long AudioCaptureManager::Configure(bool desktop_enabled, bool microphone_enabled,
                                    PacketCallback callback, std::wstring render_id, std::wstring microphone_id,
                                    uint32_t game_process_id, uint32_t chat_process_id,
                                    uint32_t music_process_id) {
    Stop();
    std::scoped_lock lock(impl_->mutex);
    for (auto& meter : impl_->meters) meter.store(0.f, std::memory_order_relaxed);
    impl_->callback = std::move(callback);
    impl_->want_desktop = desktop_enabled;
    impl_->want_microphone = microphone_enabled;
    impl_->game_pid = game_process_id; impl_->chat_pid = chat_process_id; impl_->music_pid = music_process_id;
    impl_->render_id = render_id;
    impl_->microphone_id = microphone_id;
    impl_->retry_desktop = std::chrono::steady_clock::now() + std::chrono::seconds(5);
    impl_->retry_microphone = impl_->retry_desktop;
    impl_->retry_game = impl_->retry_desktop; impl_->retry_chat = impl_->retry_desktop;
    impl_->retry_music = impl_->retry_desktop;
    if (desktop_enabled) {
        impl_->desktop = std::make_unique<WasapiWorker>();
        impl_->desktop->id = std::move(render_id); impl_->desktop->loopback = true;
        impl_->desktop->kind = core::StreamKind::desktop_audio; impl_->desktop->callback = impl_->callback;
        impl_->desktop->gain = &impl_->gains[0];
        impl_->desktop->meter = &impl_->meters[0];
        impl_->desktop->Start();
    }
    if (microphone_enabled) {
        impl_->microphone = std::make_unique<WasapiWorker>();
        impl_->microphone->id = std::move(microphone_id); impl_->microphone->kind = core::StreamKind::microphone_audio;
        impl_->microphone->callback = impl_->callback; impl_->microphone->gain = &impl_->gains[1];
        impl_->microphone->meter = &impl_->meters[1];
        impl_->microphone->Start();
    }
    if (game_process_id) {
        impl_->game = std::make_unique<WasapiWorker>();
        impl_->game->process_id = game_process_id; impl_->game->kind = core::StreamKind::game_audio;
        impl_->game->callback = impl_->callback; impl_->game->gain = &impl_->gains[2];
        impl_->game->meter = &impl_->meters[2]; impl_->game->Start();
    }
    if (chat_process_id) {
        impl_->chat = std::make_unique<WasapiWorker>();
        impl_->chat->process_id = chat_process_id; impl_->chat->kind = core::StreamKind::chat_audio;
        impl_->chat->callback = impl_->callback; impl_->chat->gain = &impl_->gains[3];
        impl_->chat->meter = &impl_->meters[3]; impl_->chat->Start();
    }
    if (music_process_id) {
        impl_->music = std::make_unique<WasapiWorker>();
        impl_->music->process_id = music_process_id; impl_->music->kind = core::StreamKind::music_audio;
        impl_->music->callback = impl_->callback; impl_->music->gain = &impl_->gains[4];
        impl_->music->meter = &impl_->meters[4]; impl_->music->Start();
    }
    return S_OK;
}
void AudioCaptureManager::Stop() noexcept {
    if (!impl_) return;
    std::scoped_lock lock(impl_->mutex);
    if (impl_->desktop) { impl_->desktop->Stop(); impl_->desktop.reset(); }
    if (impl_->microphone) { impl_->microphone->Stop(); impl_->microphone.reset(); }
    if (impl_->game) { impl_->game->Stop(); impl_->game.reset(); }
    if (impl_->chat) { impl_->chat->Stop(); impl_->chat.reset(); }
    if (impl_->music) { impl_->music->Stop(); impl_->music.reset(); }
    for (auto& meter : impl_->meters) meter.store(0.f, std::memory_order_relaxed);
}
std::array<float, 5> AudioCaptureManager::TakeLevels() noexcept {
    std::array<float, 5> levels{};
    for (std::size_t i = 0; i < levels.size(); ++i)
        levels[i] = impl_->meters[i].exchange(0.f, std::memory_order_relaxed);
    return levels;
}
long AudioCaptureManager::SetGain(core::StreamKind kind, float gain) noexcept {
    const auto index = static_cast<int>(kind) - static_cast<int>(core::StreamKind::desktop_audio);
    if (index < 0 || index >= static_cast<int>(impl_->gains.size()) || !std::isfinite(gain) || gain < 0.f || gain > 2.f)
        return E_INVALIDARG;
    impl_->gains[index].store(gain, std::memory_order_relaxed);
    return S_OK;
}
AudioCaptureStatus AudioCaptureManager::Status() noexcept {
    std::scoped_lock lock(impl_->mutex);
    const auto now = std::chrono::steady_clock::now();
    auto retry = [&](std::unique_ptr<WasapiWorker>& worker, bool wanted, bool loopback,
                     core::StreamKind kind, std::wstring const& id,
                     std::chrono::steady_clock::time_point& next_retry) {
        if (!wanted || !worker || worker->running || !worker->error || now < next_retry) return;
        worker->Stop(); worker.reset();
        try {
            worker = std::make_unique<WasapiWorker>();
            worker->id = id; worker->loopback = loopback; worker->kind = kind;
            worker->gain = &impl_->gains[static_cast<int>(kind) - 1];
            worker->meter = &impl_->meters[static_cast<int>(kind) - 1];
            worker->callback = impl_->callback; worker->Start();
        } catch (...) { worker.reset(); }
        next_retry = now + std::chrono::seconds(5);
    };
    retry(impl_->desktop, impl_->want_desktop, true, core::StreamKind::desktop_audio,
          impl_->render_id, impl_->retry_desktop);
    retry(impl_->microphone, impl_->want_microphone, false, core::StreamKind::microphone_audio,
          impl_->microphone_id, impl_->retry_microphone);
    auto retry_process = [&](std::unique_ptr<WasapiWorker>& worker, DWORD pid, core::StreamKind kind,
                             std::chrono::steady_clock::time_point& next_retry) {
        if (!pid || !worker || worker->running || !worker->error || now < next_retry) return;
        worker->Stop(); worker.reset();
        try {
            worker = std::make_unique<WasapiWorker>(); worker->process_id = pid;
            worker->kind = kind; worker->callback = impl_->callback;
            worker->gain = &impl_->gains[static_cast<int>(kind) - 1];
            worker->meter = &impl_->meters[static_cast<int>(kind) - 1]; worker->Start();
        } catch (...) { worker.reset(); }
        next_retry = now + std::chrono::seconds(5);
    };
    retry_process(impl_->game, impl_->game_pid, core::StreamKind::game_audio, impl_->retry_game);
    retry_process(impl_->chat, impl_->chat_pid, core::StreamKind::chat_audio, impl_->retry_chat);
    retry_process(impl_->music, impl_->music_pid, core::StreamKind::music_audio, impl_->retry_music);
    return {impl_->desktop && impl_->desktop->running.load(), impl_->microphone && impl_->microphone->running.load(),
        impl_->desktop ? impl_->desktop->error.load() : 0, impl_->microphone ? impl_->microphone->error.load() : 0,
        impl_->game && impl_->game->running.load(), impl_->chat && impl_->chat->running.load(),
        impl_->game ? impl_->game->error.load() : 0, impl_->chat ? impl_->chat->error.load() : 0,
        impl_->music && impl_->music->running.load(), impl_->music ? impl_->music->error.load() : 0};
}

} // namespace clippr::capture
