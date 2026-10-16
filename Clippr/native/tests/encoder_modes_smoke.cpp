#include "clippr/encode/video_encoder.hpp"

#include <d3d11.h>
#include <wrl/client.h>
#include <vector>
#include <cstdio>

using Microsoft::WRL::ComPtr;

int wmain(int argc, wchar_t** argv) {
    if (argc != 3 && argc != 4) return 2;
    const bool packet_mode = argc == 4 && wcscmp(argv[3], L"packet") == 0;
    const auto mode = static_cast<clippr::encode::H264EncoderMode>(_wtoi(argv[1]));
    ComPtr<ID3D11Device> device;
    ComPtr<ID3D11DeviceContext> context;
    D3D_FEATURE_LEVEL feature{};
    const HRESULT created = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
        D3D11_CREATE_DEVICE_BGRA_SUPPORT | D3D11_CREATE_DEVICE_VIDEO_SUPPORT, nullptr, 0,
        D3D11_SDK_VERSION, device.GetAddressOf(), &feature, context.GetAddressOf());
    if (FAILED(created)) { std::printf("device: 0x%08lX\n", created); return 3; }

    constexpr UINT width = 640, height = 360;
    std::vector<unsigned char> pixels(width * height * 4, 64);
    for (UINT i = 3; i < pixels.size(); i += 4) pixels[i] = 255;
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = desc.ArraySize = desc.SampleDesc.Count = 1;
    desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = 0;
    D3D11_SUBRESOURCE_DATA data{pixels.data(), width * 4, 0};
    ComPtr<ID3D11Texture2D> frame;
    HRESULT hr = device->CreateTexture2D(&desc, &data, frame.GetAddressOf());
    if (FAILED(hr)) { std::printf("frame: 0x%08lX\n", hr); return 4; }

    clippr::encode::VideoEncodingConfig config{width, height, 30, 2'000'000, mode};
    std::unique_ptr<clippr::encode::IVideoEncoder> encoder;
    unsigned packet_count = 0;
    if (packet_mode) {
        hr = clippr::encode::CreateH264PacketEncoder(device.Get(), config,
            [&packet_count](clippr::core::EncodedPacket&& packet) {
                if (!packet.bytes.empty()) ++packet_count;
            }, encoder);
    } else {
        hr = clippr::encode::CreateH264Encoder(device.Get(), config, argv[2], encoder);
    }
    if (FAILED(hr)) { std::printf("init: 0x%08lX\n", hr); return 5; }
    std::wprintf(L"backend: %ls\n", encoder->Backend());
    for (int i = 0; i < 30; ++i) {
        hr = encoder->Write(frame.Get(), i * (10'000'000LL / 30));
        if (FAILED(hr)) { std::printf("write %d stage %d: 0x%08lX\n", i, encoder->Stage(), hr); return 6; }
    }
    hr = encoder->Finish();
    if (FAILED(hr)) { std::printf("finish: 0x%08lX\n", hr); return 7; }
    if (packet_mode && packet_count < 5) {
        std::printf("only %u packets\n", packet_count);
        return 8;
    }
    return 0;
}
