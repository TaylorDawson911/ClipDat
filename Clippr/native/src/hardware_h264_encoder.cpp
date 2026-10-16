#include "clippr/encode/video_encoder.hpp"

#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <mftransform.h>
#include <d3d11_4.h>
#include <dxgi1_2.h>
#include <wrl/client.h>
#include <wrl/implements.h>

#include <algorithm>
#include <memory>
#include <cstring>

using Microsoft::WRL::ComPtr;

namespace clippr::encode {
namespace {

bool HasIdrNal(BYTE const* bytes, DWORD size) noexcept {
    for (DWORD i = 0; i + 4 < size; ++i) {
        if (bytes[i] == 0 && bytes[i + 1] == 0 &&
            ((bytes[i + 2] == 1 && (bytes[i + 3] & 31) == 5) ||
             (bytes[i + 2] == 0 && bytes[i + 3] == 1 && (bytes[i + 4] & 31) == 5)))
            return true;
    }
    return false;
}

class PacketSinkCallback final : public Microsoft::WRL::RuntimeClass<
    Microsoft::WRL::RuntimeClassFlags<Microsoft::WRL::ClassicCom>, IMFSampleGrabberSinkCallback2> {
    PacketCallback callback_;
public:
    explicit PacketSinkCallback(PacketCallback callback) : callback_(std::move(callback)) {}
    STDMETHODIMP OnClockStart(MFTIME, LONGLONG) override { return S_OK; }
    STDMETHODIMP OnClockStop(MFTIME) override { return S_OK; }
    STDMETHODIMP OnClockPause(MFTIME) override { return S_OK; }
    STDMETHODIMP OnClockRestart(MFTIME) override { return S_OK; }
    STDMETHODIMP OnClockSetRate(MFTIME, float) override { return S_OK; }
    STDMETHODIMP OnSetPresentationClock(IMFPresentationClock*) override { return S_OK; }
    STDMETHODIMP OnShutdown() override { return S_OK; }
    STDMETHODIMP OnProcessSample(REFGUID major, DWORD flags, LONGLONG time,
                                 LONGLONG duration, BYTE const* bytes, DWORD size) override {
        return OnProcessSampleEx(major, flags, time, duration, bytes, size, nullptr);
    }
    STDMETHODIMP OnProcessSampleEx(REFGUID major, DWORD, LONGLONG time,
                                   LONGLONG duration, BYTE const* bytes, DWORD size,
                                   IMFAttributes* attributes) override {
        if (major != MFMediaType_Video || !bytes || !size || time == _I64_MAX) return E_INVALIDARG;
        try {
            clippr::core::EncodedPacket packet;
            packet.stream = clippr::core::StreamKind::video;
            packet.timestamp = clippr::core::MediaTime{time};
            packet.duration = clippr::core::MediaTime{duration == _I64_MAX ? 0 : duration};
            packet.keyframe = (attributes && MFGetAttributeUINT32(attributes,
                MFSampleExtension_CleanPoint, FALSE)) || HasIdrNal(bytes, size);
            if (attributes) {
                UINT64 decode_time{};
                if (SUCCEEDED(attributes->GetUINT64(MFSampleExtension_DecodeTimestamp, &decode_time)))
                    packet.decode_timestamp = clippr::core::MediaTime{static_cast<int64_t>(decode_time)};
            }
            packet.bytes.resize(size);
            std::memcpy(packet.bytes.data(), bytes, size);
            callback_(std::move(packet));
            return S_OK;
        } catch (...) { return E_FAIL; }
    }
};

class MfH264Encoder final : public IVideoEncoder {
    VideoEncodingConfig config_{};
    ComPtr<ID3D11VideoDevice> video_device_;
    ComPtr<ID3D11VideoContext> video_context_;
    ComPtr<ID3D11VideoProcessorEnumerator> enumerator_;
    ComPtr<ID3D11VideoProcessor> processor_;
    ComPtr<ID3D11Device> device_;
    ComPtr<ID3D11Texture2D> staging_texture_;
    UINT input_width_{};
    UINT input_height_{};
    ComPtr<IMFDXGIDeviceManager> manager_;
    ComPtr<IMFMediaSink> packet_sink_;
    ComPtr<PacketSinkCallback> packet_callback_;
    ComPtr<IMFSinkWriter> writer_;
    DWORD stream_{};
    int64_t first_time_{-1};
    int64_t last_time_{-1};
    bool started_{};
    bool finished_{};
    bool mf_started_{};
    int stage_{};
    bool hardware_{};

public:
    ~MfH264Encoder() override {
        if (started_ && !finished_) (void)Finish();
        writer_.Reset();
        manager_.Reset();
        if (mf_started_) (void)MFShutdown();
    }

    wchar_t const* Backend() const noexcept override {
        return hardware_ ? L"Media Foundation hardware H.264" : L"Media Foundation software H.264";
    }
    int Stage() const noexcept override { return stage_; }

    HRESULT Initialize(ID3D11Device* device, VideoEncodingConfig config,
                       std::wstring const& path, PacketCallback callback = {}) {
        if (!device || (path.empty() && !callback) || !config.width || !config.height ||
            (config.width & 1) || (config.height & 1) ||
            !config.fps || config.fps > 240 || config.bitrate < 100'000)
            return E_INVALIDARG;
        config_ = config;
        device_ = device;
        HRESULT hr = MFStartup(MF_VERSION, MFSTARTUP_FULL);
        if (FAILED(hr)) return hr;
        mf_started_ = true;

        ComPtr<ID3D11DeviceContext> context;
        device->GetImmediateContext(context.GetAddressOf());
        if (FAILED(hr = device->QueryInterface(IID_PPV_ARGS(video_device_.GetAddressOf())))) return hr;
        if (FAILED(hr = context.As(&video_context_))) return hr;

        ComPtr<IMFAttributes> attributes;
        if (FAILED(hr = MFCreateAttributes(attributes.GetAddressOf(), 3))) return hr;
        if (config.mode == H264EncoderMode::hardware) {
            UINT reset_token{};
            if (FAILED(hr = MFCreateDXGIDeviceManager(&reset_token, manager_.GetAddressOf()))) return hr;
            if (FAILED(hr = manager_->ResetDevice(device, reset_token))) return hr;
            if (FAILED(hr = attributes->SetUnknown(MF_SINK_WRITER_D3D_MANAGER, manager_.Get()))) return hr;
            if (FAILED(hr = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, TRUE))) return hr;
        } else {
            if (FAILED(hr = attributes->SetUINT32(MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, FALSE))) return hr;
        }
        ComPtr<IMFMediaType> output_type;
        if (FAILED(hr = MFCreateMediaType(output_type.GetAddressOf()))) return hr;
        if (FAILED(hr = output_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video))) return hr;
        if (FAILED(hr = output_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_H264))) return hr;
        if (FAILED(hr = output_type->SetUINT32(MF_MT_AVG_BITRATE, config.bitrate))) return hr;
        if (FAILED(hr = output_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive))) return hr;
        if (FAILED(hr = MFSetAttributeSize(output_type.Get(), MF_MT_FRAME_SIZE, config.width, config.height))) return hr;
        if (FAILED(hr = MFSetAttributeRatio(output_type.Get(), MF_MT_FRAME_RATE, config.fps, 1))) return hr;
        if (FAILED(hr = MFSetAttributeRatio(output_type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1))) return hr;
        if (callback) {
            packet_callback_ = Microsoft::WRL::Make<PacketSinkCallback>(std::move(callback));
            if (!packet_callback_) return E_OUTOFMEMORY;
            ComPtr<IMFActivate> activation;
            if (FAILED(hr = MFCreateSampleGrabberSinkActivate(output_type.Get(), packet_callback_.Get(),
                                                               activation.GetAddressOf()))) return hr;
            if (FAILED(hr = activation->SetUINT32(MF_SAMPLEGRABBERSINK_IGNORE_CLOCK, TRUE))) return hr;
            if (FAILED(hr = activation->ActivateObject(IID_PPV_ARGS(packet_sink_.GetAddressOf())))) return hr;
            if (FAILED(hr = MFCreateSinkWriterFromMediaSink(packet_sink_.Get(), attributes.Get(),
                                                             writer_.GetAddressOf()))) return hr;
            stream_ = 0;
        } else {
            if (FAILED(hr = MFCreateSinkWriterFromURL(path.c_str(), nullptr, attributes.Get(),
                                                       writer_.GetAddressOf()))) return hr;
            if (FAILED(hr = writer_->AddStream(output_type.Get(), &stream_))) return hr;
        }

        ComPtr<IMFMediaType> input_type;
        if (FAILED(hr = MFCreateMediaType(input_type.GetAddressOf()))) return hr;
        if (FAILED(hr = input_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video))) return hr;
        if (FAILED(hr = input_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12))) return hr;
        if (FAILED(hr = input_type->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive))) return hr;
        if (FAILED(hr = MFSetAttributeSize(input_type.Get(), MF_MT_FRAME_SIZE, config.width, config.height))) return hr;
        if (FAILED(hr = MFSetAttributeRatio(input_type.Get(), MF_MT_FRAME_RATE, config.fps, 1))) return hr;
        if (FAILED(hr = MFSetAttributeRatio(input_type.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1))) return hr;
        if (FAILED(hr = writer_->SetInputMediaType(stream_, input_type.Get(), nullptr))) return hr;

        // The sink writer's hardware flag is a preference. Verify the selected transform
        // so the requested mode and reported backend match the encoder actually used.
        ComPtr<IMFSinkWriterEx> writer_ex;
        if (FAILED(hr = writer_.As(&writer_ex))) return hr;
        bool hardware_encoder = false;
        bool found_encoder = false;
        for (DWORD i = 0;; ++i) {
            GUID category{};
            ComPtr<IMFTransform> transform;
            hr = writer_ex->GetTransformForStream(stream_, i, &category, transform.GetAddressOf());
            if (FAILED(hr)) break;
            if (category != MFT_CATEGORY_VIDEO_ENCODER) continue;
            found_encoder = true;
            ComPtr<IMFAttributes> transform_attributes;
            if (SUCCEEDED(transform->GetAttributes(transform_attributes.GetAddressOf()))) {
                UINT32 length{};
                if (SUCCEEDED(transform_attributes->GetStringLength(
                        MFT_ENUM_HARDWARE_URL_Attribute, &length)) && length > 0) {
                    hardware_encoder = true;
                }
            }
        }
        if (!found_encoder || hardware_encoder != (config.mode == H264EncoderMode::hardware))
            return MF_E_TOPO_CODEC_NOT_FOUND;
        hardware_ = hardware_encoder;
        if (FAILED(hr = writer_->BeginWriting())) return hr;
        started_ = true;
        return S_OK;
    }

    long Write(ID3D11Texture2D* frame, int64_t timestamp) noexcept override {
        stage_ = 1;
        if (!started_ || finished_ || !frame) return E_UNEXPECTED;
        D3D11_TEXTURE2D_DESC input_desc{};
        frame->GetDesc(&input_desc);
        if (input_desc.Format != DXGI_FORMAT_B8G8R8A8_UNORM ||
            !input_desc.Width || !input_desc.Height)
            return MF_E_INVALIDMEDIATYPE;
        if (input_width_ != input_desc.Width || input_height_ != input_desc.Height) {
            processor_.Reset();
            enumerator_.Reset();
            staging_texture_.Reset();
            D3D11_VIDEO_PROCESSOR_CONTENT_DESC desc{};
            desc.InputFrameFormat = D3D11_VIDEO_FRAME_FORMAT_PROGRESSIVE;
            desc.InputFrameRate = {config_.fps, 1};
            desc.InputWidth = input_desc.Width;
            desc.InputHeight = input_desc.Height;
            desc.OutputFrameRate = {config_.fps, 1};
            desc.OutputWidth = config_.width;
            desc.OutputHeight = config_.height;
            desc.Usage = D3D11_VIDEO_USAGE_PLAYBACK_NORMAL;
            HRESULT setup = video_device_->CreateVideoProcessorEnumerator(&desc, enumerator_.GetAddressOf());
            if (FAILED(setup)) return setup;
            UINT format_support{};
            if (FAILED(setup = enumerator_->CheckVideoProcessorFormat(DXGI_FORMAT_B8G8R8A8_UNORM, &format_support))) return setup;
            if (!(format_support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_INPUT)) return MF_E_INVALIDMEDIATYPE;
            if (FAILED(setup = enumerator_->CheckVideoProcessorFormat(DXGI_FORMAT_NV12, &format_support))) return setup;
            if (!(format_support & D3D11_VIDEO_PROCESSOR_FORMAT_SUPPORT_OUTPUT)) return MF_E_INVALIDMEDIATYPE;
            if (FAILED(setup = video_device_->CreateVideoProcessor(enumerator_.Get(), 0, processor_.GetAddressOf()))) return setup;
            input_width_ = input_desc.Width;
            input_height_ = input_desc.Height;
        }
        stage_ = 2;
        ComPtr<ID3D11VideoProcessorInputView> input_view;
        D3D11_VIDEO_PROCESSOR_INPUT_VIEW_DESC view_desc{};
        view_desc.FourCC = 0;
        view_desc.ViewDimension = D3D11_VPIV_DIMENSION_TEXTURE2D;
        view_desc.Texture2D.MipSlice = 0;
        HRESULT hr = video_device_->CreateVideoProcessorInputView(frame, enumerator_.Get(),
                                                                    &view_desc, input_view.GetAddressOf());
        if (FAILED(hr)) return hr;
        stage_ = 3;
        D3D11_TEXTURE2D_DESC texture{};
        texture.Width = config_.width;
        texture.Height = config_.height;
        texture.MipLevels = 1;
        texture.ArraySize = 1;
        texture.Format = DXGI_FORMAT_NV12;
        texture.SampleDesc.Count = 1;
        texture.Usage = D3D11_USAGE_DEFAULT;
        texture.BindFlags = D3D11_BIND_RENDER_TARGET;
        ComPtr<ID3D11Texture2D> nv12;
        if (FAILED(hr = device_->CreateTexture2D(&texture, nullptr, nv12.GetAddressOf()))) return hr;
        stage_ = 4;
        D3D11_VIDEO_PROCESSOR_OUTPUT_VIEW_DESC output_desc{};
        output_desc.ViewDimension = D3D11_VPOV_DIMENSION_TEXTURE2D;
        output_desc.Texture2D.MipSlice = 0;
        ComPtr<ID3D11VideoProcessorOutputView> output_view;
        if (FAILED(hr = video_device_->CreateVideoProcessorOutputView(nv12.Get(), enumerator_.Get(),
                                                                       &output_desc, output_view.GetAddressOf()))) return hr;
        stage_ = 5;
        D3D11_VIDEO_PROCESSOR_STREAM stream{};
        stream.Enable = TRUE;
        stream.pInputSurface = input_view.Get();
        if (FAILED(hr = video_context_->VideoProcessorBlt(processor_.Get(), output_view.Get(), 0, 1, &stream))) return hr;
        stage_ = 6;

        ComPtr<IMFMediaBuffer> buffer;
        if (hardware_) {
            if (FAILED(hr = MFCreateDXGISurfaceBuffer(__uuidof(ID3D11Texture2D), nv12.Get(), 0, FALSE,
                                                       buffer.GetAddressOf()))) return hr;
            DWORD max_length{};
            if (FAILED(hr = buffer->GetMaxLength(&max_length))) return hr;
            if (FAILED(hr = buffer->SetCurrentLength(max_length))) return hr;
        } else {
            // The software MFT consumes system memory. Copy the GPU NV12 frame row by row,
            // since a mapped staging texture can have a wider row pitch than the image.
            D3D11_TEXTURE2D_DESC staging_desc = texture;
            staging_desc.Usage = D3D11_USAGE_STAGING;
            staging_desc.BindFlags = 0;
            staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
            if (!staging_texture_ &&
                FAILED(hr = device_->CreateTexture2D(&staging_desc, nullptr, staging_texture_.GetAddressOf()))) return hr;
            ComPtr<ID3D11DeviceContext> context;
            device_->GetImmediateContext(context.GetAddressOf());
            context->CopyResource(staging_texture_.Get(), nv12.Get());
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if (FAILED(hr = context->Map(staging_texture_.Get(), 0, D3D11_MAP_READ, 0, &mapped))) return hr;
            const DWORD size = config_.width * config_.height * 3 / 2;
            hr = MFCreateMemoryBuffer(size, buffer.GetAddressOf());
            BYTE* bytes{};
            if (SUCCEEDED(hr)) hr = buffer->Lock(&bytes, nullptr, nullptr);
            if (SUCCEEDED(hr)) {
                auto* source = static_cast<BYTE const*>(mapped.pData);
                for (uint32_t row = 0; row < config_.height * 3 / 2; ++row)
                    std::memcpy(bytes + row * config_.width, source + row * mapped.RowPitch, config_.width);
                (void)buffer->Unlock();
                hr = buffer->SetCurrentLength(size);
            }
            context->Unmap(staging_texture_.Get(), 0);
            if (FAILED(hr)) return hr;
        }
        stage_ = 7;
        ComPtr<IMFSample> sample;
        if (FAILED(hr = MFCreateSample(sample.GetAddressOf()))) return hr;
        if (FAILED(hr = sample->AddBuffer(buffer.Get()))) return hr;
        stage_ = 8;
        if (first_time_ < 0) first_time_ = timestamp;
        int64_t relative = timestamp - first_time_;
        relative = std::max(relative, last_time_ + 1);
        if (FAILED(hr = sample->SetSampleTime(relative))) return hr;
        if (FAILED(hr = sample->SetSampleDuration(10'000'000LL / config_.fps))) return hr;
        stage_ = 9;
        if (FAILED(hr = writer_->WriteSample(stream_, sample.Get()))) return hr;
        last_time_ = relative;
        return S_OK;
    }

    long Finish() noexcept override {
        if (!started_ || finished_) return S_OK;
        finished_ = true;
        return writer_->Finalize();
    }
};

} // namespace

namespace {

long CreateEncoder(ID3D11Device* device, VideoEncodingConfig config,
                   std::wstring const& path, PacketCallback callback,
                   std::unique_ptr<IVideoEncoder>& result) noexcept {
    result.reset();
    try {
        const auto requested_mode = config.mode;
        HRESULT hr = MF_E_TOPO_CODEC_NOT_FOUND;
        for (auto mode : {H264EncoderMode::hardware, H264EncoderMode::software}) {
            if (requested_mode != H264EncoderMode::automatic && requested_mode != mode) continue;
            config.mode = mode;
            auto encoder = std::make_unique<MfH264Encoder>();
            hr = encoder->Initialize(device, config, path, callback);
            if (SUCCEEDED(hr)) { result = std::move(encoder); return S_OK; }
        }
        return hr;
    } catch (...) { return E_OUTOFMEMORY; }
}

} // namespace

long CreateH264Encoder(ID3D11Device* device, VideoEncodingConfig config,
                       std::wstring const& path,
                       std::unique_ptr<IVideoEncoder>& result) noexcept {
    return CreateEncoder(device, config, path, {}, result);
}

long CreateH264PacketEncoder(ID3D11Device* device, VideoEncodingConfig config,
                             PacketCallback callback,
                             std::unique_ptr<IVideoEncoder>& result) noexcept {
    return CreateEncoder(device, config, {}, std::move(callback), result);
}

} // namespace clippr::encode
