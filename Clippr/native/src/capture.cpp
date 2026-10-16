#include "clippr/capture/capture_api.h"
#include "clippr/encode/video_encoder.hpp"
#include "clippr/capture/audio_capture.hpp"
#include "clippr/core/replay_buffer.hpp"
#include "clippr/replay/replay_export.hpp"

#include <Windows.h>
#include <Windows.Graphics.Capture.Interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <microsoft.ui.xaml.media.dxinterop.h>
#include <d2d1_1.h>
#include <d2d1_1helper.h>
#include <d3d11.h>
#include <d3d11_4.h>
#include <dwmapi.h>
#include <dxgi1_2.h>
#include <mferror.h>
#include <shellapi.h>
#include <shlobj.h>
#include <tlhelp32.h>
#include <winver.h>
#include <wincodec.h>
#include <wrl/client.h>

#include <winrt/base.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Metadata.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>

#include <atomic>
#include <algorithm>
#include <condition_variable>
#include <chrono>
#include <deque>
#include <filesystem>
#include <list>
#include <memory>
#include <mutex>
#include <new>
#include <string>
#include <thread>
#include <unordered_map>
#include <utility>
#include <vector>

using Microsoft::WRL::ComPtr;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace direct3d = winrt::Windows::Graphics::DirectX::Direct3D11;

namespace {

bool ProcessTreeContains(DWORD ancestor, DWORD process) noexcept {
    if (!ancestor || !process) return false;
    HANDLE snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
    if (snapshot == INVALID_HANDLE_VALUE) return false;
    std::unordered_map<DWORD, DWORD> parents;
    PROCESSENTRY32W entry{}; entry.dwSize = sizeof(entry);
    if (Process32FirstW(snapshot, &entry)) do { parents.emplace(entry.th32ProcessID, entry.th32ParentProcessID); }
        while (Process32NextW(snapshot, &entry));
    CloseHandle(snapshot);
    for (DWORD current = process, depth = 0; current && depth < 128; ++depth) {
        if (current == ancestor) return true;
        const auto found = parents.find(current);
        if (found == parents.end() || found->second == current) break;
        current = found->second;
    }
    return false;
}

bool EligibleWindow(HWND window) noexcept {
    if (!IsWindow(window) || !IsWindowVisible(window) ||
        GetAncestor(window, GA_ROOT) != window ||
        GetWindowTextLengthW(window) == 0 ||
        (GetWindowLongPtrW(window, GWL_EXSTYLE) & WS_EX_TOOLWINDOW)) return false;
    DWORD process_id{};
    GetWindowThreadProcessId(window, &process_id);
    if (process_id == GetCurrentProcessId()) return false;
    BOOL cloaked{};
    if (SUCCEEDED(DwmGetWindowAttribute(window, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked)
        return false;
    return true;
}

std::wstring ProcessPath(DWORD process_id) {
    std::wstring path(32768, L'\0');
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, process_id);
    if (!process) return {};
    DWORD size = static_cast<DWORD>(path.size());
    const bool success = QueryFullProcessImageNameW(process, 0, path.data(), &size) != FALSE;
    CloseHandle(process);
    if (!success) return {};
    path.resize(size);
    return path;
}

std::wstring ApplicationName(std::wstring const& path, std::wstring const& title) {
    if (path.empty()) return title;
    DWORD ignored{};
    const DWORD bytes = GetFileVersionInfoSizeW(path.c_str(), &ignored);
    if (bytes) {
        std::vector<std::byte> data(bytes);
        if (GetFileVersionInfoW(path.c_str(), 0, bytes, data.data())) {
            struct Language { WORD language; WORD codepage; };
            Language* languages{};
            UINT size{};
            if (VerQueryValueW(data.data(), L"\\VarFileInfo\\Translation",
                               reinterpret_cast<void**>(&languages), &size) && size >= sizeof(Language)) {
                wchar_t key[80]{};
                swprintf_s(key, L"\\StringFileInfo\\%04x%04x\\FileDescription",
                           languages[0].language, languages[0].codepage);
                wchar_t* description{};
                if (VerQueryValueW(data.data(), key, reinterpret_cast<void**>(&description), &size) &&
                    description && description[0]) return description;
            }
        }
    }
    auto start = path.find_last_of(L"\\/");
    std::wstring name = path.substr(start == std::wstring::npos ? 0 : start + 1);
    if (name.size() > 4 && _wcsicmp(name.c_str() + name.size() - 4, L".exe") == 0)
        name.resize(name.size() - 4);
    return name;
}

struct CaptureComponent final : std::enable_shared_from_this<CaptureComponent> {
    std::mutex mutex;
    ComPtr<ID3D11Device> d3d;
    ComPtr<ID3D11DeviceContext> context;
    ComPtr<ID2D1Device> d2d;
    ComPtr<ID2D1DeviceContext> drawing;
    ComPtr<ID2D1Bitmap1> target;
    ComPtr<IDXGISwapChain1> swapchain;
    ComPtr<ISwapChainPanelNative> panel;
    direct3d::IDirect3DDevice winrt_device{nullptr};
    wgc::GraphicsCaptureItem item{nullptr};
    wgc::Direct3D11CaptureFramePool pool{nullptr};
    wgc::GraphicsCaptureSession session{nullptr};
    winrt::event_token arrived_token{};
    winrt::event_token closed_token{};
    std::atomic<int32_t> state{CLIPPR_CAPTURE_STOPPED};
    std::atomic<int32_t> error{0};
    std::atomic<uint64_t> frames{0};
    std::atomic<HWND> active_window{nullptr};
    std::atomic<bool> window_was_minimized{false};
    int32_t preview_width{};
    int32_t preview_height{};
    winrt::Windows::Graphics::SizeInt32 capture_size{};
    std::mutex encoding_mutex;
    std::condition_variable encoding_ready;
    struct QueuedFrame {
        ComPtr<ID3D11Texture2D> texture;
        int64_t timestamp{};
        std::chrono::steady_clock::time_point enqueued_at{};
    };
    std::deque<QueuedFrame> encoding_queue;
    std::thread encoding_thread;
    bool encoding_stopping{};
    int64_t last_queued_timestamp{-1};
    clippr::encode::VideoEncodingConfig encoding_config{};
    clippr::encode::H264EncoderMode encoder_mode{clippr::encode::H264EncoderMode::hardware};
    std::atomic<int32_t> active_hardware_encoders{0};
    std::atomic<int32_t> active_software_encoders{0};
    std::wstring encoding_path;
    std::atomic<int32_t> encoding_state{0};
    std::atomic<int32_t> encoding_error{0};
    std::atomic<int32_t> encoding_error_stage{0};
    std::atomic<uint64_t> encoded_frames{0};
    std::atomic<uint64_t> dropped_frames{0};
    std::atomic<uint64_t> rate_limited_frames{0};
    std::atomic<uint64_t> write_latency_last_us{0};
    std::atomic<uint64_t> write_latency_total_us{0};
    std::atomic<uint64_t> write_latency_samples{0};
    std::atomic<uint64_t> write_latency_max_us{0};
    std::atomic<uint64_t> queue_latency_total_us{0};
    std::atomic<uint64_t> queue_latency_samples{0};
    std::atomic<uint64_t> queue_latency_max_us{0};
    clippr::core::ReplayLimits replay_limits{};
    std::shared_ptr<clippr::core::ReplayBuffer> replay_buffer;
    std::mutex replay_mutex;
    std::condition_variable replay_ready;
    std::deque<QueuedFrame> replay_queue;
    std::thread replay_thread;
    struct SaveJob { std::thread thread; std::atomic<bool> done{false}; };
    std::list<SaveJob> save_jobs;
    bool replay_stopping{};
    int64_t last_replay_timestamp{-1};
    clippr::encode::VideoEncodingConfig replay_config{};
    uint32_t replay_bitrate{8'000'000};
    std::atomic<int32_t> replay_state{0};
    std::atomic<bool> replay_enabled{true};
    std::atomic<bool> replay_audio_timeline_ready{false};
    std::atomic<int64_t> replay_audio_timestamp_offset{};
    std::atomic<int32_t> replay_error{0};
    std::atomic<uint64_t> replay_dropped{0};
    std::atomic<uint64_t> replay_encoded_frames{0};
    std::atomic<uint64_t> saves_completed{0};
    std::atomic<uint64_t> saves_failed{0};
    std::atomic<int32_t> last_save_error{0};
    std::atomic<int32_t> saves_active{0};
    std::atomic<int32_t> last_save_shorter{0};
    std::atomic<uint64_t> last_save_duration_ms{0};
    std::wstring last_replay_path;
    clippr::capture::AudioCaptureManager audio_capture;
    std::atomic<bool> desktop_audio_enabled{true};
    std::atomic<bool> microphone_audio_enabled{false};
    std::atomic<DWORD> game_audio_pid{0};
    std::atomic<DWORD> chat_audio_pid{0};
    std::atomic<DWORD> music_audio_pid{0};
    std::mutex audio_config_mutex;
    std::wstring audio_render_id;
    std::wstring audio_microphone_id;
    std::thread hotkey_thread;
    std::atomic<DWORD> hotkey_thread_id{0};
    std::atomic<bool> hotkey_ready{false};
    std::atomic<UINT> hotkey_modifiers{MOD_CONTROL | MOD_SHIFT};
    std::atomic<UINT> hotkey_virtual_key{VK_F10};
    std::atomic<HRESULT> hotkey_error{S_OK};
    std::atomic<bool> route_replay_hotkey_to_app{false};
    std::atomic<uint64_t> replay_hotkey_requests{0};
    std::atomic<uint32_t> replay_seconds{30};
    std::wstring replay_output_folder;
    std::wstring replay_output_extension{L"mp4"};
    int replay_filename_mode{}; // 0 = ClipDat, 1 = timestamp, 2 = application
    std::wstring replay_filename_source{L"ClipDat"};
    struct ScreenshotRequest { ComPtr<ID3D11Texture2D> texture; HRESULT result{E_PENDING}; };
    std::mutex screenshot_mutex;
    std::condition_variable screenshot_ready;
    std::shared_ptr<ScreenshotRequest> screenshot_request;
    std::atomic<bool> screenshot_busy{false};
    std::atomic<uint64_t> screenshots_completed{0};
    std::atomic<HRESULT> screenshot_error{S_OK};
    std::mutex screenshot_config_mutex;
    std::wstring screenshot_folder;
    bool screenshot_jpeg{};
    uint32_t screenshot_quality{90};
    UINT screenshot_modifiers{MOD_CONTROL | MOD_ALT};
    UINT screenshot_virtual_key{VK_F10};
    std::wstring last_screenshot_path;

    HRESULT EncodeScreenshot(ID3D11Texture2D* texture, const wchar_t* path,
                             bool jpeg, uint32_t quality) noexcept {
        if (!texture || !path || !*path) return E_INVALIDARG;
        HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        const bool uninitialize = SUCCEEDED(com);
        if (FAILED(com) && com != RPC_E_CHANGED_MODE) return com;
        HRESULT hr = E_FAIL;
        std::filesystem::path temporary_path;
        try {
            D3D11_TEXTURE2D_DESC desc{}; texture->GetDesc(&desc);
            if (!desc.Width || !desc.Height || desc.Width > 16384 || desc.Height > 16384) { hr = E_INVALIDARG; goto done; }
            std::vector<BYTE> pixels;
            {
                // The capture callback uses the same immediate D3D context. Serialize the
                // one-off readback with rendering, then release it before WIC encoding.
                std::scoped_lock context_lock(mutex);
                D3D11_TEXTURE2D_DESC staging_desc = desc;
                staging_desc.BindFlags = 0; staging_desc.MiscFlags = 0;
                staging_desc.Usage = D3D11_USAGE_STAGING; staging_desc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                ComPtr<ID3D11Texture2D> staging;
                hr = d3d->CreateTexture2D(&staging_desc, nullptr, &staging);
                if (FAILED(hr)) goto done;
                pixels.resize(static_cast<size_t>(desc.Width) * desc.Height * 4);
                context->CopyResource(staging.Get(), texture);
                D3D11_MAPPED_SUBRESOURCE mapped{};
                hr = context->Map(staging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
                if (FAILED(hr)) goto done;
                for (UINT y = 0; y < desc.Height; ++y)
                    memcpy(pixels.data() + static_cast<size_t>(y) * desc.Width * 4,
                           static_cast<const BYTE*>(mapped.pData) + static_cast<size_t>(y) * mapped.RowPitch,
                           static_cast<size_t>(desc.Width) * 4);
                context->Unmap(staging.Get(), 0);
            }
            {
                const std::filesystem::path final_path(path);
                temporary_path = final_path; temporary_path += L".tmp";
                ComPtr<IWICImagingFactory> factory;
                hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER,
                                      IID_PPV_ARGS(&factory));
                if (FAILED(hr)) goto done;
                ComPtr<IWICStream> stream;
                hr = factory->CreateStream(&stream);
                if (FAILED(hr)) goto done;
                hr = stream->InitializeFromFilename(temporary_path.c_str(), GENERIC_WRITE);
                if (FAILED(hr)) goto done;
                ComPtr<IWICBitmapEncoder> encoder;
                hr = factory->CreateEncoder(jpeg ? GUID_ContainerFormatJpeg : GUID_ContainerFormatPng,
                                             nullptr, &encoder);
                if (FAILED(hr)) goto done;
                hr = encoder->Initialize(stream.Get(), WICBitmapEncoderNoCache);
                if (FAILED(hr)) goto done;
                ComPtr<IWICBitmapFrameEncode> frame; ComPtr<IPropertyBag2> options;
                hr = encoder->CreateNewFrame(&frame, &options);
                if (FAILED(hr)) goto done;
                if (jpeg && options) {
                    PROPBAG2 option{}; option.pstrName = const_cast<LPOLESTR>(L"ImageQuality");
                    VARIANT value; VariantInit(&value); value.vt = VT_R4;
                    value.fltVal = static_cast<float>(std::clamp(quality, 1u, 100u)) / 100.0f;
                    options->Write(1, &option, &value); VariantClear(&value);
                }
                hr = frame->Initialize(options.Get()); if (FAILED(hr)) goto done;
                hr = frame->SetSize(desc.Width, desc.Height); if (FAILED(hr)) goto done;
                WICPixelFormatGUID pixel_format = jpeg ? GUID_WICPixelFormat24bppBGR : GUID_WICPixelFormat32bppBGRA;
                hr = frame->SetPixelFormat(&pixel_format); if (FAILED(hr)) goto done;
                ComPtr<IWICBitmap> bitmap;
                hr = factory->CreateBitmapFromMemory(desc.Width, desc.Height, GUID_WICPixelFormat32bppBGRA,
                    desc.Width * 4, static_cast<UINT>(pixels.size()), pixels.data(), &bitmap);
                if (FAILED(hr)) goto done;
                ComPtr<IWICFormatConverter> converter;
                hr = factory->CreateFormatConverter(&converter); if (FAILED(hr)) goto done;
                hr = converter->Initialize(bitmap.Get(), pixel_format, WICBitmapDitherTypeNone, nullptr, 0.0,
                                           WICBitmapPaletteTypeCustom);
                if (FAILED(hr)) goto done;
                hr = frame->WriteSource(converter.Get(), nullptr); if (FAILED(hr)) goto done;
                hr = frame->Commit(); if (FAILED(hr)) goto done;
                hr = encoder->Commit(); if (FAILED(hr)) goto done;
                stream.Reset(); frame.Reset(); encoder.Reset();
                if (!MoveFileExW(temporary_path.c_str(), final_path.c_str(), MOVEFILE_WRITE_THROUGH)) {
                    hr = HRESULT_FROM_WIN32(GetLastError()); DeleteFileW(temporary_path.c_str()); goto done;
                }
            }
        } catch (std::bad_alloc const&) { hr = E_OUTOFMEMORY; }
          catch (...) { hr = E_FAIL; }
    done:
        if (FAILED(hr) && !temporary_path.empty()) DeleteFileW(temporary_path.c_str());
        if (uninitialize) CoUninitialize();
        return hr;
    }

    HRESULT SaveScreenshot(const wchar_t* path, bool jpeg, uint32_t quality) noexcept {
        if (!path || !*path || quality < 1 || quality > 100) return E_INVALIDARG;
        screenshot_error = S_OK;
        bool expected = false;
        if (!screenshot_busy.compare_exchange_strong(expected, true)) return HRESULT_FROM_WIN32(ERROR_BUSY);
        struct Release { std::atomic<bool>& flag; ~Release() { flag = false; } } release{screenshot_busy};
        if (state.load() != CLIPPR_CAPTURE_RUNNING) return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        std::shared_ptr<ScreenshotRequest> request;
        try { request = std::make_shared<ScreenshotRequest>(); }
        catch (...) { return E_OUTOFMEMORY; }
        { std::scoped_lock lock(screenshot_mutex); screenshot_request = request; }
        std::unique_lock lock(screenshot_mutex);
        const bool ready = screenshot_ready.wait_for(lock, std::chrono::seconds(4), [&] {
            return request->result != E_PENDING || state.load() != CLIPPR_CAPTURE_RUNNING;
        });
        if (!ready) { screenshot_request.reset(); return HRESULT_FROM_WIN32(ERROR_TIMEOUT); }
        auto texture = request->texture; auto result = request->result;
        screenshot_request.reset(); lock.unlock();
        if (FAILED(result)) return result;
        result = EncodeScreenshot(texture.Get(), path, jpeg, quality);
        if (SUCCEEDED(result)) {
            { std::scoped_lock config_lock(screenshot_config_mutex); last_screenshot_path = path; }
            screenshot_error = S_OK;
            screenshots_completed.fetch_add(1, std::memory_order_release);
        } else screenshot_error = result;
        return result;
    }

    void RecordEncoderWrite(std::chrono::steady_clock::time_point started) noexcept {
        const auto elapsed = static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now() - started).count());
        write_latency_last_us.store(elapsed, std::memory_order_relaxed);
        write_latency_total_us.fetch_add(elapsed, std::memory_order_relaxed);
        write_latency_samples.fetch_add(1, std::memory_order_relaxed);
        auto peak = write_latency_max_us.load(std::memory_order_relaxed);
        while (peak < elapsed && !write_latency_max_us.compare_exchange_weak(
            peak, elapsed, std::memory_order_relaxed)) {}
    }

    void RecordQueueWait(std::chrono::steady_clock::time_point enqueued_at) noexcept {
        const auto elapsed = static_cast<uint64_t>(std::chrono::duration_cast<std::chrono::microseconds>(
            std::chrono::steady_clock::now() - enqueued_at).count());
        queue_latency_total_us.fetch_add(elapsed, std::memory_order_relaxed);
        queue_latency_samples.fetch_add(1, std::memory_order_relaxed);
        auto peak = queue_latency_max_us.load(std::memory_order_relaxed);
        while (peak < elapsed && !queue_latency_max_us.compare_exchange_weak(
            peak, elapsed, std::memory_order_relaxed)) {}
    }

    void Initialize(IUnknown* panel_unknown, int32_t width, int32_t height) {
        if (!panel_unknown || width <= 0 || height <= 0) winrt::throw_hresult(E_INVALIDARG);
        if (!wgc::GraphicsCaptureSession::IsSupported()) {
            state = CLIPPR_CAPTURE_UNSUPPORTED;
            winrt::throw_hresult(E_NOTIMPL);
        }
        winrt::check_hresult(panel_unknown->QueryInterface(IID_PPV_ARGS(panel.ReleaseAndGetAddressOf())));

        UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
        D3D_FEATURE_LEVEL level{};
        HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
                                       nullptr, 0, D3D11_SDK_VERSION, d3d.GetAddressOf(),
                                       &level, context.GetAddressOf());
        if (FAILED(hr)) {
            winrt::check_hresult(D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_WARP, nullptr, flags,
                                                   nullptr, 0, D3D11_SDK_VERSION, d3d.GetAddressOf(),
                                                   &level, context.GetAddressOf()));
        }
        ComPtr<ID3D11Multithread> multithread;
        winrt::check_hresult(context.As(&multithread));
        multithread->SetMultithreadProtected(TRUE);

        ComPtr<IDXGIDevice> dxgi_device;
        winrt::check_hresult(d3d.As(&dxgi_device));
        winrt::check_hresult(D2D1CreateDevice(dxgi_device.Get(), nullptr, d2d.GetAddressOf()));
        winrt::check_hresult(d2d->CreateDeviceContext(D2D1_DEVICE_CONTEXT_OPTIONS_NONE, drawing.GetAddressOf()));
        drawing->SetUnitMode(D2D1_UNIT_MODE_PIXELS);

        ComPtr<IDXGIAdapter> adapter;
        ComPtr<IDXGIFactory2> factory;
        winrt::check_hresult(dxgi_device->GetAdapter(adapter.GetAddressOf()));
        winrt::check_hresult(adapter->GetParent(IID_PPV_ARGS(factory.GetAddressOf())));
        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = static_cast<UINT>(width);
        desc.Height = static_cast<UINT>(height);
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        desc.BufferCount = 2;
        desc.Scaling = DXGI_SCALING_STRETCH;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        desc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;
        winrt::check_hresult(factory->CreateSwapChainForComposition(d3d.Get(), &desc, nullptr,
                                                                     swapchain.GetAddressOf()));
        preview_width = width;
        preview_height = height;
        CreateTarget();
        // Initialize is called by WinUI on the panel's UI thread.
        winrt::check_hresult(panel->SetSwapChain(swapchain.Get()));

        winrt::com_ptr<IInspectable> inspectable;
        winrt::check_hresult(CreateDirect3D11DeviceFromDXGIDevice(dxgi_device.Get(), inspectable.put()));
        winrt_device = inspectable.as<direct3d::IDirect3DDevice>();
        hotkey_thread = std::thread([this] { HotkeyLoop(); });
    }

    void CreateTarget() {
        ComPtr<IDXGISurface> surface;
        winrt::check_hresult(swapchain->GetBuffer(0, IID_PPV_ARGS(surface.GetAddressOf())));
        auto properties = D2D1::BitmapProperties1(
            D2D1_BITMAP_OPTIONS_TARGET | D2D1_BITMAP_OPTIONS_CANNOT_DRAW,
            D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_IGNORE));
        winrt::check_hresult(drawing->CreateBitmapFromDxgiSurface(surface.Get(), &properties,
                                                                   target.ReleaseAndGetAddressOf()));
        drawing->SetTarget(target.Get());
    }

    void StopCapture() noexcept {
        (void)StopEncoding();
        StopReplay();
        wgc::GraphicsCaptureSession old_session{nullptr};
        wgc::Direct3D11CaptureFramePool old_pool{nullptr};
        wgc::GraphicsCaptureItem old_item{nullptr};
        {
            std::scoped_lock lock(mutex);
            state = CLIPPR_CAPTURE_STOPPED;
            active_window = nullptr;
            window_was_minimized = false;
            try { if (pool) pool.FrameArrived(arrived_token); } catch (...) {}
            try { if (item) item.Closed(closed_token); } catch (...) {}
            old_session = std::exchange(session, nullptr);
            old_pool = std::exchange(pool, nullptr);
            old_item = std::exchange(item, nullptr);
        }
        { std::scoped_lock lock(screenshot_mutex); if (screenshot_request) {
              screenshot_request->result = HRESULT_FROM_WIN32(ERROR_CANCELLED); screenshot_request.reset(); } }
        screenshot_ready.notify_all();
        try { if (old_session) old_session.Close(); } catch (...) {}
        try { if (old_pool) old_pool.Close(); } catch (...) {}
    }

    void StartCapture(HMONITOR monitor) {
        StopCapture();
        if (!monitor) winrt::throw_hresult(E_INVALIDARG);
        MONITORINFOEXW info{sizeof(info)};
        if (!GetMonitorInfoW(monitor, &info)) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_MONITOR_HANDLE));
        auto factory = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        wgc::GraphicsCaptureItem new_item{nullptr};
        winrt::check_hresult(factory->CreateForMonitor(monitor, winrt::guid_of<wgc::GraphicsCaptureItem>(),
                                                       winrt::put_abi(new_item)));
        StartItem(new_item, nullptr);
    }

    void StartWindowCapture(HWND window) {
        StopCapture();
        if (!EligibleWindow(window)) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE));
        if (IsIconic(window)) winrt::throw_hresult(HRESULT_FROM_WIN32(ERROR_NOT_READY));
        auto factory = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        wgc::GraphicsCaptureItem new_item{nullptr};
        winrt::check_hresult(factory->CreateForWindow(window, winrt::guid_of<wgc::GraphicsCaptureItem>(),
                                                      winrt::put_abi(new_item)));
        StartItem(new_item, window);
    }

    void StartItem(wgc::GraphicsCaptureItem const& new_item, HWND window) {
        auto size = new_item.Size();
        if (size.Width <= 0 || size.Height <= 0) winrt::throw_hresult(E_FAIL);
        if (winrt::Windows::Foundation::Metadata::ApiInformation::IsTypePresent(
                L"Windows.Graphics.Capture.GraphicsCaptureAccess")) {
            static std::once_flag borderless_access_request;
            std::call_once(borderless_access_request, [] {
                try {
                    (void)wgc::GraphicsCaptureAccess::RequestAccessAsync(
                        wgc::GraphicsCaptureAccessKind::Borderless).get();
                } catch (...) {
                    // Capture continues with the Windows border if access is unavailable.
                }
            });
        }
        auto new_pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(
            winrt_device, winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
            2, size);
        auto new_session = new_pool.CreateCaptureSession(new_item);
        if (winrt::Windows::Foundation::Metadata::ApiInformation::IsPropertyPresent(
                L"Windows.Graphics.Capture.GraphicsCaptureSession", L"IsBorderRequired")) {
            new_session.IsBorderRequired(false);
        }
        auto weak = weak_from_this();
        auto new_arrived = new_pool.FrameArrived([weak](auto const& source, auto const&) {
            if (auto self = weak.lock()) self->OnFrame(source);
        });
        auto new_closed = new_item.Closed([weak](auto const&, auto const&) {
            if (auto self = weak.lock()) self->state = CLIPPR_CAPTURE_DISCONNECTED;
        });
        {
            std::scoped_lock lock(mutex);
            item = new_item;
            pool = new_pool;
            session = new_session;
            arrived_token = new_arrived;
            closed_token = new_closed;
            capture_size = size;
            active_window = window;
            window_was_minimized = false;
            frames = 0;
            error = 0;
            state = CLIPPR_CAPTURE_RUNNING;
        }
        try {
            new_session.StartCapture();
            if (replay_enabled.load()) StartReplay();
        } catch (...) {
            StopCapture();
            throw;
        }
    }

    void OnFrame(wgc::Direct3D11CaptureFramePool const& source) noexcept {
        try {
            std::scoped_lock lock(mutex);
            if (state != CLIPPR_CAPTURE_RUNNING || source != pool) return;
            auto frame = source.TryGetNextFrame();
            if (!frame) return;
            const auto size = frame.ContentSize();
            if (size.Width <= 0 || size.Height <= 0) {
                state = CLIPPR_CAPTURE_DISCONNECTED;
                return;
            }
            if (auto window = active_window.load(); window && IsIconic(window)) {
                window_was_minimized = true;
                return;
            }
            auto surface = frame.Surface();
            auto access = surface.as<Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
            ComPtr<IDXGISurface> dxgi_surface;
            winrt::check_hresult(access->GetInterface(IID_PPV_ARGS(dxgi_surface.GetAddressOf())));
            {
                std::shared_ptr<ScreenshotRequest> request;
                { std::scoped_lock request_lock(screenshot_mutex); request = screenshot_request; }
                if (request) {
                    ComPtr<ID3D11Texture2D> source_texture;
                    HRESULT copy_result = dxgi_surface.As(&source_texture);
                    ComPtr<ID3D11Texture2D> snapshot;
                    if (SUCCEEDED(copy_result)) {
                        D3D11_TEXTURE2D_DESC desc{}; source_texture->GetDesc(&desc);
                        desc.BindFlags = 0; desc.MiscFlags = 0; desc.Usage = D3D11_USAGE_DEFAULT;
                        desc.CPUAccessFlags = 0;
                        copy_result = d3d->CreateTexture2D(&desc, nullptr, &snapshot);
                        if (SUCCEEDED(copy_result)) context->CopyResource(snapshot.Get(), source_texture.Get());
                    }
                    { std::scoped_lock request_lock(screenshot_mutex); request->texture = std::move(snapshot); request->result = copy_result; }
                    screenshot_ready.notify_all();
                }
            }
            if (encoding_state == 1 || encoding_state == 2 || replay_state == 1 || replay_state == 2) {
                ComPtr<ID3D11Texture2D> source_texture;
                if (SUCCEEDED(dxgi_surface.As(&source_texture))) {
                    const auto timestamp = frame.SystemRelativeTime().count();
                    if ((replay_state == 1 || replay_state == 2) && !replay_audio_timeline_ready.load(std::memory_order_acquire)) {
                        LARGE_INTEGER qpc{}, frequency{};
                        if (QueryPerformanceCounter(&qpc) && QueryPerformanceFrequency(&frequency) && frequency.QuadPart > 0) {
                            const auto whole = qpc.QuadPart / frequency.QuadPart;
                            const auto remainder = qpc.QuadPart % frequency.QuadPart;
                            const auto audio_now = whole * 10'000'000LL +
                                (remainder * 10'000'000LL) / frequency.QuadPart;
                            // The video encoder rebases its first input frame to PTS zero.
                            // Apply the same origin to the absolute WASAPI QPC timestamps.
                            replay_audio_timestamp_offset.store(-audio_now, std::memory_order_relaxed);
                            replay_audio_timeline_ready.store(true, std::memory_order_release);
                        }
                    }
                    QueueVideoFrame(source_texture.Get(), timestamp,
                        encoding_state == 1 || encoding_state == 2,
                        replay_state == 1 || replay_state == 2);
                }
            }
            auto properties = D2D1::BitmapProperties1(D2D1_BITMAP_OPTIONS_NONE,
                D2D1::PixelFormat(DXGI_FORMAT_B8G8R8A8_UNORM, D2D1_ALPHA_MODE_PREMULTIPLIED));
            ComPtr<ID2D1Bitmap1> image;
            winrt::check_hresult(drawing->CreateBitmapFromDxgiSurface(dxgi_surface.Get(), &properties,
                                                                       image.GetAddressOf()));
            drawing->BeginDraw();
            drawing->Clear(D2D1::ColorF(D2D1::ColorF::Black));
            // Fit the entire captured surface inside the preview at its original aspect ratio.
            const float scale = std::min(static_cast<float>(preview_width) / size.Width,
                                         static_cast<float>(preview_height) / size.Height);
            const float display_width = size.Width * scale;
            const float display_height = size.Height * scale;
            const float display_left = (preview_width - display_width) * 0.5f;
            const float display_top = (preview_height - display_height) * 0.5f;
            drawing->DrawBitmap(image.Get(),
                                D2D1::RectF(display_left, display_top,
                                            display_left + display_width, display_top + display_height),
                                1.0f, D2D1_INTERPOLATION_MODE_LINEAR,
                                D2D1::RectF(0, 0, static_cast<float>(size.Width), static_cast<float>(size.Height)));
            winrt::check_hresult(drawing->EndDraw());
            winrt::check_hresult(swapchain->Present(0, 0));
            ++frames;
            window_was_minimized = false;
            image.Reset();
            dxgi_surface.Reset();
            access = nullptr;
            surface = nullptr;
            frame.Close();
            if (size.Width > 0 && size.Height > 0 &&
                (size.Width != capture_size.Width || size.Height != capture_size.Height)) {
                pool.Recreate(winrt_device,
                    winrt::Windows::Graphics::DirectX::DirectXPixelFormat::B8G8R8A8UIntNormalized,
                    2, size);
                capture_size = size;
            }
        } catch (winrt::hresult_error const& ex) {
            error = ex.code();
            state = CLIPPR_CAPTURE_ERROR;
        } catch (...) {
            error = E_FAIL;
            state = CLIPPR_CAPTURE_ERROR;
        }
    }

    long StartEncoding(wchar_t const* path, uint32_t fps, uint32_t bitrate,
                       uint32_t width, uint32_t height) noexcept {
        if (!path || !*path || !fps || !bitrate) return E_INVALIDARG;
        std::scoped_lock capture_lock(mutex);
        if (state != CLIPPR_CAPTURE_RUNNING) return E_UNEXPECTED;
        if (!width && !height) {
            width = static_cast<uint32_t>(capture_size.Width) & ~1u;
            height = static_cast<uint32_t>(capture_size.Height) & ~1u;
        }
        if (!width || !height || (width & 1) || (height & 1)) return E_INVALIDARG;
        if (encoding_state != 0 && encoding_state != 3) return HRESULT_FROM_WIN32(ERROR_BUSY);
        if (encoding_thread.joinable()) return HRESULT_FROM_WIN32(ERROR_BUSY);
        encoding_config = {width, height, fps, bitrate, encoder_mode};
        encoding_path = path;
        {
            std::scoped_lock queue_lock(encoding_mutex);
            encoding_queue.clear();
            encoding_stopping = false;
            last_queued_timestamp = -1;
        }
        encoded_frames = 0;
        dropped_frames = 0;
        rate_limited_frames = 0;
        write_latency_last_us = 0;
        write_latency_total_us = 0;
        write_latency_samples = 0;
        write_latency_max_us = 0;
        queue_latency_total_us = 0;
        queue_latency_samples = 0;
        queue_latency_max_us = 0;
        encoding_error = 0;
        encoding_error_stage = 0;
        encoding_state = 1;
        try {
            encoding_thread = std::thread([this] { EncodeLoop(); });
        } catch (...) {
            encoding_state = 3;
            encoding_error = E_OUTOFMEMORY;
            return E_OUTOFMEMORY;
        }
        return S_OK;
    }

    void QueueVideoFrame(ID3D11Texture2D* frame, int64_t timestamp,
                         bool feed_recording, bool feed_replay) noexcept {
        if (!feed_recording && !feed_replay) return;

        bool enqueue_recording = false;
        bool enqueue_replay = false;
        {
            // Both consumers retain the same copied GPU texture. The two queues
            // remain independently bounded and own independent COM references.
            std::scoped_lock lock(encoding_mutex, replay_mutex);
            if (feed_recording) {
                const int64_t interval = 10'000'000LL / encoding_config.fps;
                if (last_queued_timestamp < 0 || timestamp - last_queued_timestamp >= interval) {
                    if (encoding_queue.size() < 2) enqueue_recording = true;
                    else ++dropped_frames;
                } else {
                    ++rate_limited_frames;
                }
            }
            if (feed_replay) {
                const int64_t interval = 10'000'000LL / replay_config.fps;
                if (last_replay_timestamp < 0 || timestamp - last_replay_timestamp >= interval) {
                    if (replay_queue.size() < 2) enqueue_replay = true;
                    else ++replay_dropped;
                }
            }
            if (!enqueue_recording && !enqueue_replay) return;

            D3D11_TEXTURE2D_DESC desc{};
            frame->GetDesc(&desc);
            desc.MipLevels = 1;
            desc.ArraySize = 1;
            desc.BindFlags = 0;
            desc.CPUAccessFlags = 0;
            desc.MiscFlags = 0;
            ComPtr<ID3D11Texture2D> retained;
            if (FAILED(d3d->CreateTexture2D(&desc, nullptr, retained.GetAddressOf()))) {
                if (enqueue_recording) ++dropped_frames;
                if (enqueue_replay) ++replay_dropped;
                return;
            }
            context->CopyResource(retained.Get(), frame);
            const auto enqueued_at = std::chrono::steady_clock::now();
            if (enqueue_recording) {
                encoding_queue.push_back({retained, timestamp, enqueued_at});
                last_queued_timestamp = timestamp;
            }
            if (enqueue_replay) {
                replay_queue.push_back({retained, timestamp, enqueued_at});
                last_replay_timestamp = timestamp;
            }
        }
        if (enqueue_recording) encoding_ready.notify_one();
        if (enqueue_replay) replay_ready.notify_one();
    }

    void EncodeLoop() noexcept {
        const HRESULT com_result = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(com_result)) {
            encoding_error = com_result;
            encoding_state = 3;
            return;
        }
        std::unique_ptr<clippr::encode::IVideoEncoder> encoder;
        const HRESULT initialize = clippr::encode::CreateH264Encoder(
            d3d.Get(), encoding_config, encoding_path, encoder);
        if (FAILED(initialize)) {
            encoding_error = initialize;
            encoding_state = 3;
            CoUninitialize();
            return;
        }
        const bool hardware_encoder = wcscmp(encoder->Backend(), L"Media Foundation hardware H.264") == 0;
        if (hardware_encoder) ++active_hardware_encoders; else ++active_software_encoders;
        encoding_state = 2;
        const int64_t minimum_interval = 10'000'000LL / encoding_config.fps;
        int64_t last_encoded_time = -1;
        for (;;) {
            QueuedFrame queued;
            {
                std::unique_lock lock(encoding_mutex);
                encoding_ready.wait(lock, [this] {
                    return encoding_stopping || encoding_state == 3 || !encoding_queue.empty();
                });
                if (encoding_queue.empty()) {
                    if (encoding_stopping || encoding_state == 3) break;
                    continue;
                }
                queued = std::move(encoding_queue.front());
                encoding_queue.pop_front();
            }
            RecordQueueWait(queued.enqueued_at);
            if (last_encoded_time >= 0 && queued.timestamp - last_encoded_time < minimum_interval) {
                ++rate_limited_frames;
                continue;
            }
            const auto write_started = std::chrono::steady_clock::now();
            const HRESULT hr = static_cast<HRESULT>(encoder->Write(queued.texture.Get(), queued.timestamp));
            RecordEncoderWrite(write_started);
            if (FAILED(hr)) {
                encoding_error = hr;
                encoding_error_stage = encoder->Stage();
                encoding_state = 3;
                break;
            }
            last_encoded_time = queued.timestamp;
            ++encoded_frames;
        }
        const HRESULT finish = static_cast<HRESULT>(encoder->Finish());
        if (FAILED(finish) && encoding_state != 3) {
            encoding_error = finish;
            encoding_state = 3;
        }
        std::scoped_lock lock(encoding_mutex);
        encoding_queue.clear();
        if (hardware_encoder) --active_hardware_encoders; else --active_software_encoders;
        encoder.reset();
        CoUninitialize();
    }

    long StopEncoding() noexcept {
        if (!encoding_thread.joinable()) return encoding_error.load();
        if (encoding_state != 3) encoding_state = 4;
        {
            std::scoped_lock lock(encoding_mutex);
            encoding_stopping = true;
        }
        encoding_ready.notify_one();
        encoding_thread.join();
        if (encoding_state != 3) encoding_state = 0;
        return encoding_error.load();
    }

    long ConfigureReplay(uint32_t duration, uint64_t ram, uint64_t disk, uint64_t minimum_free,
                         uint32_t bitrate) noexcept {
        if (!duration || duration > 3600 || ram < 1024 * 1024 || disk > 100ULL * 1024 * 1024 * 1024 ||
            minimum_free < 1024ULL * 1024 * 1024 || minimum_free > 100ULL * 1024 * 1024 * 1024 ||
            bitrate < 100'000 || bitrate > 100'000'000)
            return E_INVALIDARG;
        std::scoped_lock capture_lock(mutex);
        StopReplay();
        replay_limits.duration = std::chrono::seconds(duration);
        replay_seconds = duration;
        replay_limits.ram_bytes = static_cast<std::size_t>(ram);
        replay_limits.disk_bytes = static_cast<std::size_t>(disk);
        replay_limits.minimum_free_disk_bytes = minimum_free;
        replay_bitrate = bitrate;
        if (state == CLIPPR_CAPTURE_RUNNING && replay_enabled.load()) StartReplay();
        return replay_error.load();
    }

    long ConfigureEncoder(int32_t mode) noexcept {
        if (mode < 0 || mode > 2) return E_INVALIDARG;
        std::scoped_lock capture_lock(mutex);
        if (encoder_mode == static_cast<clippr::encode::H264EncoderMode>(mode)) return S_OK;
        encoder_mode = static_cast<clippr::encode::H264EncoderMode>(mode);
        // A direct recording keeps its current encoder until the next recording starts.
        StopReplay();
        if (state == CLIPPR_CAPTURE_RUNNING && replay_enabled.load()) StartReplay();
        return replay_state == 3 ? replay_error.load() : S_OK;
    }

    long SetReplayEnabled(bool enabled) noexcept {
        std::scoped_lock capture_lock(mutex);
        replay_enabled = enabled;
        if (!enabled) { StopReplay(); replay_error = 0; return S_OK; }
        if (replay_state == 3) StopReplay();
        if (state == CLIPPR_CAPTURE_RUNNING && replay_state == 0) StartReplay();
        return replay_state == 3 ? replay_error.load() : S_OK;
    }

    long ConfigureReplayOutput(wchar_t const* folder, wchar_t const* extension) noexcept {
        if (!folder || !*folder || !extension ||
            (_wcsicmp(extension, L"mp4") != 0 && _wcsicmp(extension, L"mkv") != 0)) return E_INVALIDARG;
        try {
            std::scoped_lock lock(replay_mutex);
            replay_output_folder = folder;
            replay_output_extension = extension;
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }

    long ConfigureReplayFilename(int mode, wchar_t const* source) noexcept {
        if (mode < 0 || mode > 2 || !source || wcslen(source) > 64 ||
            wcspbrk(source, L"\\/:*?\"<>|") != nullptr) return E_INVALIDARG;
        try {
            std::scoped_lock lock(replay_mutex);
            replay_filename_mode = mode;
            replay_filename_source = source;
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }

    void StartReplayImpl() {
        if (replay_thread.joinable()) return;
        if (replay_limits.disk_directory.empty()) {
            wchar_t temp[MAX_PATH]{};
            if (!GetTempPathW(MAX_PATH, temp)) { replay_error = HRESULT_FROM_WIN32(GetLastError()); replay_state = 3; return; }
            replay_limits.disk_directory = std::filesystem::path(temp) / L"ClipprReplay" /
                std::to_wstring(GetCurrentProcessId()) /
                std::to_wstring(reinterpret_cast<uintptr_t>(this));
        }
        const auto width = static_cast<uint32_t>(capture_size.Width) & ~1u;
        const auto height = static_cast<uint32_t>(capture_size.Height) & ~1u;
        if (!width || !height) { replay_error = E_INVALIDARG; replay_state = 3; return; }
        {
            std::scoped_lock lock(replay_mutex);
            replay_config = {width, height, 30, replay_bitrate, encoder_mode};
            replay_buffer = std::make_shared<clippr::core::ReplayBuffer>(replay_limits);
            replay_queue.clear();
            replay_stopping = false;
            last_replay_timestamp = -1;
        }
        replay_error = 0;
        replay_dropped = 0;
        replay_encoded_frames = 0;
        replay_audio_timeline_ready.store(false, std::memory_order_release);
        replay_audio_timestamp_offset.store(0, std::memory_order_relaxed);
        replay_state = 1;
        std::wstring render_id, microphone_id;
        { std::scoped_lock lock(audio_config_mutex); render_id = audio_render_id; microphone_id = audio_microphone_id; }
        const auto game_pid = game_audio_pid.load(), chat_pid = chat_audio_pid.load();
        const auto music_pid = music_audio_pid.load();
        const auto audio_hr = audio_capture.Configure(desktop_audio_enabled.load() && !game_pid && !chat_pid && !music_pid,
            microphone_audio_enabled.load(),
            [this, buffer = replay_buffer](clippr::core::EncodedPacket&& packet) {
                if (packet.stream != clippr::core::StreamKind::video) {
                    if (!replay_audio_timeline_ready.load(std::memory_order_acquire)) return;
                    const auto offset = clippr::core::MediaTime{
                        replay_audio_timestamp_offset.load(std::memory_order_relaxed)};
                    packet.timestamp += offset;
                    if (packet.decode_timestamp) *packet.decode_timestamp += offset;
                }
                (void)buffer->push(std::move(packet));
            }, std::move(render_id), std::move(microphone_id), game_pid, chat_pid, music_pid);
        if (FAILED(audio_hr)) replay_error = audio_hr;
        try { replay_thread = std::thread([this] { ReplayLoop(); }); }
        catch (...) { replay_error = E_OUTOFMEMORY; replay_state = 3; }
    }

    void StartReplay() noexcept {
        try { StartReplayImpl(); }
        catch (...) { replay_error = E_OUTOFMEMORY; replay_state = 3; }
    }

    void ReplayLoop() noexcept {
        const HRESULT com = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        if (FAILED(com)) { replay_error = com; replay_state = 3; return; }
        std::unique_ptr<clippr::encode::IVideoEncoder> encoder;
        std::shared_ptr<clippr::core::ReplayBuffer> buffer;
        { std::scoped_lock lock(replay_mutex); buffer = replay_buffer; }
        const HRESULT init = clippr::encode::CreateH264PacketEncoder(
            d3d.Get(), replay_config,
            [buffer, this](clippr::core::EncodedPacket&& packet) {
                if (auto pushed = buffer->push(std::move(packet)); !pushed) {
                    replay_error = E_FAIL;
                    replay_state = 3;
                }
            }, encoder);
        if (FAILED(init)) { replay_error = init; replay_state = 3; CoUninitialize(); return; }
        const bool hardware_encoder = wcscmp(encoder->Backend(), L"Media Foundation hardware H.264") == 0;
        if (hardware_encoder) ++active_hardware_encoders; else ++active_software_encoders;
        replay_state = 2;
        for (;;) {
            QueuedFrame queued;
            {
                std::unique_lock lock(replay_mutex);
                replay_ready.wait(lock, [this] { return replay_stopping || replay_state == 3 || !replay_queue.empty(); });
                if (replay_queue.empty()) break;
                queued = std::move(replay_queue.front());
                replay_queue.pop_front();
            }
            RecordQueueWait(queued.enqueued_at);
            const auto write_started = std::chrono::steady_clock::now();
            const HRESULT hr = static_cast<HRESULT>(encoder->Write(queued.texture.Get(), queued.timestamp));
            RecordEncoderWrite(write_started);
            if (FAILED(hr)) { replay_error = hr; replay_state = 3; break; }
            ++replay_encoded_frames;
        }
        const HRESULT finish = static_cast<HRESULT>(encoder->Finish());
        if (FAILED(finish) && replay_state != 3) { replay_error = finish; replay_state = 3; }
        if (hardware_encoder) --active_hardware_encoders; else --active_software_encoders;
        encoder.reset();
        CoUninitialize();
    }

    void StopReplay() noexcept {
        audio_capture.Stop();
        if (replay_thread.joinable()) {
            {
                std::scoped_lock lock(replay_mutex);
                replay_stopping = true;
                replay_queue.clear();
            }
            replay_ready.notify_one();
            replay_thread.join();
        }
        replay_state = 0;
        { std::scoped_lock lock(replay_mutex); replay_buffer.reset(); }
    }

    long ConfigureAudio(bool desktop, bool microphone) noexcept {
        desktop_audio_enabled = desktop;
        microphone_audio_enabled = microphone;
        if (replay_state != 2 && replay_state != 1) return S_OK;
        std::shared_ptr<clippr::core::ReplayBuffer> buffer;
        { std::scoped_lock lock(replay_mutex); buffer = replay_buffer; }
        if (!buffer) return S_OK;
        std::wstring render_id, microphone_id;
        { std::scoped_lock lock(audio_config_mutex); render_id = audio_render_id; microphone_id = audio_microphone_id; }
        const auto game_pid = game_audio_pid.load(), chat_pid = chat_audio_pid.load();
        const auto music_pid = music_audio_pid.load();
        return audio_capture.Configure(desktop && !game_pid && !chat_pid && !music_pid, microphone,
            [this, buffer](clippr::core::EncodedPacket&& packet) {
                if (!replay_audio_timeline_ready.load(std::memory_order_acquire)) return;
                const auto offset = clippr::core::MediaTime{
                    replay_audio_timestamp_offset.load(std::memory_order_relaxed)};
                packet.timestamp += offset;
                if (packet.decode_timestamp) *packet.decode_timestamp += offset;
                (void)buffer->push(std::move(packet));
            },
            std::move(render_id), std::move(microphone_id), game_pid, chat_pid, music_pid);
    }

    long SetAudioDevices(wchar_t const* render, wchar_t const* microphone) noexcept {
        { std::scoped_lock lock(audio_config_mutex); audio_render_id = render ? render : L""; audio_microphone_id = microphone ? microphone : L""; }
        return S_OK;
    }

    long ConfigureProcessAudio(DWORD game, DWORD chat) noexcept {
        const auto music = music_audio_pid.load();
        if ((game && chat && (ProcessTreeContains(game, chat) || ProcessTreeContains(chat, game))) ||
            (music && ((game && (ProcessTreeContains(game, music) || ProcessTreeContains(music, game))) ||
                       (chat && (ProcessTreeContains(chat, music) || ProcessTreeContains(music, chat)))))) return E_INVALIDARG;
        game_audio_pid = game;
        chat_audio_pid = chat;
        return ConfigureAudio(desktop_audio_enabled.load(), microphone_audio_enabled.load());
    }

    long ConfigureMusicAudio(DWORD music) noexcept {
        const auto game = game_audio_pid.load(), chat = chat_audio_pid.load();
        if (music && ((game && (ProcessTreeContains(game, music) || ProcessTreeContains(music, game))) ||
                      (chat && (ProcessTreeContains(chat, music) || ProcessTreeContains(music, chat))))) return E_INVALIDARG;
        music_audio_pid = music;
        return ConfigureAudio(desktop_audio_enabled.load(), microphone_audio_enabled.load());
    }

    long SaveReplay(uint32_t duration, wchar_t const* path) {
        if (!duration || !path || !*path) return E_INVALIDARG;
        std::shared_ptr<clippr::core::ReplayBuffer> buffer;
        { std::scoped_lock lock(replay_mutex); buffer = replay_buffer; }
        if (!buffer || replay_state != 2) return E_UNEXPECTED;
        auto snapshot = buffer->snapshot(std::chrono::seconds(duration));
        if (!snapshot) return HRESULT_FROM_WIN32(ERROR_NOT_READY);
        bool counted = false;
        bool appended = false;
        try {
            std::scoped_lock lock(replay_mutex);
            if (saves_active >= 3) return HRESULT_FROM_WIN32(ERROR_BUSY);
            for (auto it = save_jobs.begin(); it != save_jobs.end();) {
                if (it->done) { if (it->thread.joinable()) it->thread.join(); it = save_jobs.erase(it); }
                else ++it;
            }
            auto video = replay_config;
            save_jobs.emplace_back();
            appended = true;
            auto& job = save_jobs.back();
            ++saves_active;
            counted = true;
            job.thread = std::thread([this, &job, snapshot = std::move(*snapshot), video, path = std::wstring(path)]() {
                const auto save_started = std::chrono::steady_clock::now();
                const auto result = clippr::replay::ExportReplayFile(snapshot, video, path);
                last_save_duration_ms.store(static_cast<uint64_t>(
                    std::chrono::duration_cast<std::chrono::milliseconds>(
                        std::chrono::steady_clock::now() - save_started).count()), std::memory_order_relaxed);
                last_save_error = result;
                last_save_shorter = snapshot.shorter_than_requested ? 1 : 0;
                if (SUCCEEDED(result)) {
                    std::scoped_lock path_lock(replay_mutex);
                    last_replay_path = path;
                }
                if (FAILED(result)) ++saves_failed;
                else ++saves_completed;
                --saves_active;
                job.done = true;
            });
            return S_OK;
        } catch (...) {
            std::scoped_lock lock(replay_mutex);
            if (counted) --saves_active;
            if (appended && !save_jobs.back().thread.joinable()) save_jobs.pop_back();
            return E_OUTOFMEMORY;
        }
    }

    long ConfigureHotkey(UINT modifiers, UINT virtual_key) noexcept {
        constexpr UINT supported = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;
        if (!virtual_key || (modifiers & ~supported) || !(modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN)))
            return E_INVALIDARG;
        hotkey_modifiers = modifiers;
        hotkey_virtual_key = virtual_key;
        const auto id = hotkey_thread_id.load(std::memory_order_acquire);
        if (id && hotkey_ready.load(std::memory_order_acquire) &&
            !PostThreadMessageW(id, WM_APP + 0x417, 0, 0))
            return HRESULT_FROM_WIN32(GetLastError());
        return S_OK;
    }

    long ConfigureScreenshot(const wchar_t* folder, bool jpeg, uint32_t quality,
                             UINT modifiers, UINT virtual_key) noexcept {
        constexpr UINT supported = MOD_ALT | MOD_CONTROL | MOD_SHIFT | MOD_WIN;
        if (!folder || !*folder || quality < 1 || quality > 100 ||
            (modifiers & ~supported) || !(modifiers & (MOD_CONTROL | MOD_ALT | MOD_WIN))) return E_INVALIDARG;
        try {
            { std::scoped_lock lock(screenshot_config_mutex); screenshot_folder = folder;
              screenshot_jpeg = jpeg; screenshot_quality = quality; screenshot_modifiers = modifiers;
              screenshot_virtual_key = virtual_key; }
            const auto id = hotkey_thread_id.load(std::memory_order_acquire);
            if (id && hotkey_ready.load(std::memory_order_acquire) &&
                !PostThreadMessageW(id, WM_APP + 0x417, 0, 0)) return HRESULT_FROM_WIN32(GetLastError());
            return S_OK;
        } catch (...) { return E_OUTOFMEMORY; }
    }

    void HotkeyLoop() noexcept {
        hotkey_thread_id = GetCurrentThreadId();
        MSG message{};
        PeekMessageW(&message, nullptr, WM_USER, WM_USER, PM_NOREMOVE);
        hotkey_ready = true;
        auto register_hotkey = [this] {
            UnregisterHotKey(nullptr, 1);
            UnregisterHotKey(nullptr, 2);
            const auto modifiers = hotkey_modifiers.load(std::memory_order_acquire) | MOD_NOREPEAT;
            if (RegisterHotKey(nullptr, 1, modifiers, hotkey_virtual_key.load(std::memory_order_acquire)))
                hotkey_error = S_OK;
            else
                hotkey_error = HRESULT_FROM_WIN32(GetLastError());
            std::scoped_lock lock(screenshot_config_mutex);
            if (screenshot_virtual_key)
                RegisterHotKey(nullptr, 2, screenshot_modifiers | MOD_NOREPEAT, screenshot_virtual_key);
        };
        register_hotkey();
        while (GetMessageW(&message, nullptr, 0, 0) > 0) {
            if (message.message == WM_APP + 0x417) { register_hotkey(); continue; }
            if (message.message == WM_HOTKEY && message.wParam == 2) {
                try {
                    std::wstring folder; bool jpeg{}; uint32_t quality{};
                    { std::scoped_lock lock(screenshot_config_mutex); folder = screenshot_folder;
                      jpeg = screenshot_jpeg; quality = screenshot_quality; }
                    if (folder.empty()) continue;
                    auto dir = std::filesystem::path(folder); std::filesystem::create_directories(dir);
                    SYSTEMTIME now{}; GetLocalTime(&now); GUID guid{}; if (FAILED(CoCreateGuid(&guid))) continue;
                    wchar_t id[40]{}; StringFromGUID2(guid, id, static_cast<int>(std::size(id)));
                    wchar_t filename[128]{};
                    swprintf_s(filename, L"ClipDat-Screenshot-%04u%02u%02u-%02u%02u%02u-%03u-%s.%s",
                        now.wYear, now.wMonth, now.wDay, now.wHour, now.wMinute, now.wSecond, now.wMilliseconds,
                        id, jpeg ? L"jpg" : L"png");
                    screenshot_error = SaveScreenshot((dir / filename).c_str(), jpeg, quality);
                } catch (...) { screenshot_error = E_FAIL; }
                continue;
            }
            if (message.message != WM_HOTKEY || message.wParam != 1) continue;
            if (route_replay_hotkey_to_app.load(std::memory_order_acquire)) {
                replay_hotkey_requests.fetch_add(1, std::memory_order_relaxed);
                continue;
            }
            try {
                std::wstring output_folder, extension, source;
                int filename_mode;
                { std::scoped_lock lock(replay_mutex); output_folder = replay_output_folder; extension = replay_output_extension;
                  filename_mode = replay_filename_mode; source = replay_filename_source; }
                if (output_folder.empty()) {
                    PWSTR videos{};
                    if (FAILED(SHGetKnownFolderPath(FOLDERID_Videos, 0, nullptr, &videos))) continue;
                    output_folder = (std::filesystem::path(videos) / L"Clippr").wstring();
                    CoTaskMemFree(videos);
                }
                auto folder = std::filesystem::path(output_folder);
                std::filesystem::create_directories(folder);
                SYSTEMTIME now{};
                GetLocalTime(&now);
                GUID guid{};
                if (FAILED(CoCreateGuid(&guid))) continue;
                wchar_t unique[40]{};
                if (!StringFromGUID2(guid, unique, static_cast<int>(std::size(unique)))) continue;
                wchar_t filename[192]{};
                const wchar_t* prefix = filename_mode == 1 ? L"" : filename_mode == 2 ? source.c_str() : L"ClipDat";
                swprintf_s(filename, L"%s%s%04u-%02u-%02u %02u-%02u-%02u-%03u-%s.%s",
                    prefix, filename_mode == 1 ? L"" : L" - ", now.wYear, now.wMonth, now.wDay,
                    now.wHour, now.wMinute, now.wSecond, now.wMilliseconds, unique, extension.c_str());
                const auto path = folder / filename;
                (void)SaveReplay(replay_seconds.load(), path.c_str());
            } catch (...) {}
        }
        UnregisterHotKey(nullptr, 1);
        UnregisterHotKey(nullptr, 2);
        hotkey_ready = false;
        hotkey_thread_id = 0;
    }

    void ResizePreview(int32_t width, int32_t height) {
        if (width <= 0 || height <= 0) return;
        std::scoped_lock lock(mutex);
        if (width == preview_width && height == preview_height) return;
        drawing->SetTarget(nullptr);
        target.Reset();
        winrt::check_hresult(swapchain->ResizeBuffers(0, static_cast<UINT>(width),
                                                     static_cast<UINT>(height), DXGI_FORMAT_UNKNOWN, 0));
        preview_width = width;
        preview_height = height;
        CreateTarget();
    }

    ~CaptureComponent() {
        if (auto id = hotkey_thread_id.load(); id) PostThreadMessageW(id, WM_QUIT, 0, 0);
        if (hotkey_thread.joinable()) hotkey_thread.join();
        StopCapture();
        for (auto& save : save_jobs) if (save.thread.joinable()) save.thread.join();
        if (panel) (void)panel->SetSwapChain(nullptr);
    }
};

struct CaptureHandle {
    std::shared_ptr<CaptureComponent> component;
};

BOOL CALLBACK EnumerateMonitor(HMONITOR monitor, HDC, LPRECT, LPARAM parameter) {
    auto* result = reinterpret_cast<std::pair<ClipprDisplay*, std::pair<int32_t, int32_t>>*>(parameter);
    const auto index = result->second.first++;
    if (index >= result->second.second || !result->first) return TRUE;
    MONITORINFOEXW info{sizeof(info)};
    if (!GetMonitorInfoW(monitor, &info)) return TRUE;
    auto& display = result->first[index];
    display.monitor = monitor;
    wcscpy_s(display.device_name, info.szDevice);
    display.width = info.rcMonitor.right - info.rcMonitor.left;
    display.height = info.rcMonitor.bottom - info.rcMonitor.top;
    DISPLAY_DEVICEW device{sizeof(device)};
    if (EnumDisplayDevicesW(info.szDevice, 0, &device, 0) && device.DeviceString[0])
        wcscpy_s(display.friendly_name, device.DeviceString);
    else
        wcscpy_s(display.friendly_name, info.szDevice);
    return TRUE;
}

struct WindowEnumeration {
    ClipprWindow* windows;
    int32_t capacity;
    int32_t count;
};

BOOL CALLBACK EnumerateWindow(HWND window, LPARAM parameter) {
    if (!EligibleWindow(window)) return TRUE;
    auto& result = *reinterpret_cast<WindowEnumeration*>(parameter);
    const auto index = result.count++;
    if (index >= result.capacity || !result.windows) return TRUE;
    auto& entry = result.windows[index];
    entry.window = window;
    DWORD process_id{};
    GetWindowThreadProcessId(window, &process_id);
    entry.process_id = process_id;
    GetWindowTextW(window, entry.title, static_cast<int>(std::size(entry.title)));
    auto name = ApplicationName(ProcessPath(entry.process_id), entry.title);
    wcsncpy_s(entry.application_name, name.c_str(), _TRUNCATE);
    entry.minimized = IsIconic(window) ? 1 : 0;
    return TRUE;
}

} // namespace

extern "C" long __cdecl ClipprCreateCapture(void* panel, int32_t width, int32_t height, void** capture) {
    if (!capture) return E_POINTER;
    *capture = nullptr;
    try {
        auto handle = std::make_unique<CaptureHandle>();
        handle->component = std::make_shared<CaptureComponent>();
        handle->component->Initialize(static_cast<IUnknown*>(panel), width, height);
        *capture = handle.release();
        return S_OK;
    } catch (winrt::hresult_error const& ex) { return ex.code(); }
      catch (...) { return E_FAIL; }
}

extern "C" void __cdecl ClipprDestroyCapture(void* capture) {
    delete static_cast<CaptureHandle*>(capture);
}

extern "C" long __cdecl ClipprEnumerateDisplays(ClipprDisplay* displays, int32_t capacity, int32_t* count) {
    if (!count || capacity < 0) return E_INVALIDARG;
    std::pair<ClipprDisplay*, std::pair<int32_t, int32_t>> result{displays, {0, capacity}};
    if (!EnumDisplayMonitors(nullptr, nullptr, EnumerateMonitor, reinterpret_cast<LPARAM>(&result)))
        return HRESULT_FROM_WIN32(GetLastError());
    *count = result.second.first;
    return S_OK;
}

extern "C" long __cdecl ClipprEnumerateWindows(ClipprWindow* windows, int32_t capacity, int32_t* count) {
    if (!count || capacity < 0) return E_INVALIDARG;
    WindowEnumeration result{windows, capacity, 0};
    if (!EnumWindows(EnumerateWindow, reinterpret_cast<LPARAM>(&result)))
        return HRESULT_FROM_WIN32(GetLastError());
    *count = result.count;
    return S_OK;
}

extern "C" long __cdecl ClipprCopyWindowIcon(void* window_pointer, uint8_t* pixels, int32_t capacity) {
    if (!window_pointer || !pixels || capacity < 32 * 32 * 4) return E_INVALIDARG;
    auto window = static_cast<HWND>(window_pointer);
    if (!IsWindow(window)) return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
    DWORD_PTR icon_value{};
    SendMessageTimeoutW(window, WM_GETICON, ICON_SMALL2, 0, SMTO_ABORTIFHUNG, 100, &icon_value);
    HICON icon = reinterpret_cast<HICON>(icon_value);
    if (!icon) icon = reinterpret_cast<HICON>(GetClassLongPtrW(window, GCLP_HICONSM));
    SHFILEINFOW file_info{};
    bool owns_icon = false;
    if (!icon) {
        DWORD process_id{};
        GetWindowThreadProcessId(window, &process_id);
        auto path = ProcessPath(process_id);
        if (!path.empty() && SHGetFileInfoW(path.c_str(), 0, &file_info, sizeof(file_info),
                                            SHGFI_ICON | SHGFI_SMALLICON)) {
            icon = file_info.hIcon;
            owns_icon = true;
        }
    }
    if (!icon) return S_FALSE;
    BITMAPINFO bitmap{};
    bitmap.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bitmap.bmiHeader.biWidth = 32;
    bitmap.bmiHeader.biHeight = -32;
    bitmap.bmiHeader.biPlanes = 1;
    bitmap.bmiHeader.biBitCount = 32;
    bitmap.bmiHeader.biCompression = BI_RGB;
    void* bits{};
    HDC dc = CreateCompatibleDC(nullptr);
    HBITMAP dib = dc ? CreateDIBSection(dc, &bitmap, DIB_RGB_COLORS, &bits, nullptr, 0) : nullptr;
    if (!dib) {
        if (dc) DeleteDC(dc);
        if (owns_icon) DestroyIcon(icon);
        return E_FAIL;
    }
    auto old = SelectObject(dc, dib);
    memset(bits, 0, 32 * 32 * 4);
    const BOOL drawn = DrawIconEx(dc, 0, 0, icon, 32, 32, 0, nullptr, DI_NORMAL);
    if (drawn) memcpy(pixels, bits, 32 * 32 * 4);
    SelectObject(dc, old);
    DeleteObject(dib);
    DeleteDC(dc);
    if (owns_icon) DestroyIcon(icon);
    return drawn ? S_OK : S_FALSE;
}

extern "C" long __cdecl ClipprStartCapture(void* capture, void* monitor) {
    if (!capture) return E_POINTER;
    try { static_cast<CaptureHandle*>(capture)->component->StartCapture(static_cast<HMONITOR>(monitor)); return S_OK; }
    catch (winrt::hresult_error const& ex) { return ex.code(); }
    catch (...) { return E_FAIL; }
}

extern "C" long __cdecl ClipprStartWindowCapture(void* capture, void* window) {
    if (!capture) return E_POINTER;
    try { static_cast<CaptureHandle*>(capture)->component->StartWindowCapture(static_cast<HWND>(window)); return S_OK; }
    catch (winrt::hresult_error const& ex) { return ex.code(); }
    catch (...) { return E_FAIL; }
}

extern "C" void __cdecl ClipprStopCapture(void* capture) {
    if (capture) static_cast<CaptureHandle*>(capture)->component->StopCapture();
}

extern "C" long __cdecl ClipprSwitchDisplay(void* capture, void* monitor) {
    return ClipprStartCapture(capture, monitor);
}

extern "C" long __cdecl ClipprSwitchWindow(void* capture, void* window) {
    return ClipprStartWindowCapture(capture, window);
}

extern "C" long __cdecl ClipprResizePreview(void* capture, int32_t width, int32_t height) {
    if (!capture) return E_POINTER;
    auto& component = *static_cast<CaptureHandle*>(capture)->component;
    try { component.ResizePreview(width, height); return S_OK; }
    catch (winrt::hresult_error const& ex) {
        component.error = ex.code();
        component.state = CLIPPR_CAPTURE_ERROR;
        return ex.code();
    }
    catch (...) {
        component.error = E_FAIL;
        component.state = CLIPPR_CAPTURE_ERROR;
        return E_FAIL;
    }
}

extern "C" ClipprCaptureStatus __cdecl ClipprGetCaptureStatus(void* capture) {
    if (!capture) return {CLIPPR_CAPTURE_ERROR, E_POINTER, 0};
    auto& component = *static_cast<CaptureHandle*>(capture)->component;
    if (auto window = component.active_window.load(); window) {
        if (!IsWindow(window)) return {CLIPPR_CAPTURE_DISCONNECTED, 0, component.frames.load()};
        if (IsIconic(window)) {
            component.window_was_minimized = true;
            return {CLIPPR_CAPTURE_MINIMIZED, 0, component.frames.load()};
        }
        if (component.window_was_minimized) return {CLIPPR_CAPTURE_MINIMIZED, 0, component.frames.load()};
    }
    return {component.state.load(), component.error.load(), component.frames.load()};
}

extern "C" long __cdecl ClipprStartEncoding(void* capture, const wchar_t* path,
                                               uint32_t fps, uint32_t bitrate,
                                               uint32_t width, uint32_t height) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->StartEncoding(path, fps, bitrate, width, height);
}

extern "C" long __cdecl ClipprStopEncoding(void* capture) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->StopEncoding();
}

extern "C" ClipprEncodingStatus __cdecl ClipprGetEncodingStatus(void* capture) {
    if (!capture) return {3, E_POINTER, 0, 0, 0, 0, 0, 0, 0, 0};
    auto& component = *static_cast<CaptureHandle*>(capture)->component;
    return {component.encoding_state.load(), component.encoding_error.load(),
            component.encoded_frames.load(), component.dropped_frames.load(),
            component.rate_limited_frames.load(),
            component.encoding_error_stage.load(),
            component.write_latency_last_us.load(std::memory_order_relaxed),
            component.write_latency_samples.load(std::memory_order_relaxed)
                ? component.write_latency_total_us.load(std::memory_order_relaxed) /
                    component.write_latency_samples.load(std::memory_order_relaxed) : 0,
            component.write_latency_max_us.load(std::memory_order_relaxed),
            (component.active_hardware_encoders.load(std::memory_order_relaxed) ? 1 : 0) |
            (component.active_software_encoders.load(std::memory_order_relaxed) ? 2 : 0),
            component.queue_latency_samples.load(std::memory_order_relaxed)
                ? component.queue_latency_total_us.load(std::memory_order_relaxed) /
                    component.queue_latency_samples.load(std::memory_order_relaxed) : 0,
            component.queue_latency_max_us.load(std::memory_order_relaxed)};
}

extern "C" long __cdecl ClipprConfigureReplay(void* capture, uint32_t duration_seconds,
                                                  uint64_t ram_bytes, uint64_t disk_bytes,
                                                  uint64_t minimum_free_disk_bytes, uint32_t bitrate) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureReplay(
        duration_seconds, ram_bytes, disk_bytes, minimum_free_disk_bytes, bitrate);
}

extern "C" long __cdecl ClipprConfigureEncoder(void* capture, int32_t mode) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureEncoder(mode);
}

extern "C" long __cdecl ClipprConfigureReplayOutput(void* capture, wchar_t const* folder,
                                                       wchar_t const* extension) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureReplayOutput(folder, extension);
}

extern "C" long __cdecl ClipprConfigureReplayFilename(void* capture, int32_t mode, wchar_t const* source) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureReplayFilename(mode, source);
}

extern "C" long __cdecl ClipprSaveReplay(void* capture, uint32_t duration_seconds,
                                            wchar_t const* path) {
    if (!capture) return E_POINTER;
    try { return static_cast<CaptureHandle*>(capture)->component->SaveReplay(duration_seconds, path); }
    catch (...) { return E_OUTOFMEMORY; }
}

extern "C" ClipprReplayStatus __cdecl ClipprGetReplayStatus(void* capture) {
    if (!capture) return {3, E_POINTER, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0};
    auto& component = *static_cast<CaptureHandle*>(capture)->component;
    std::shared_ptr<clippr::core::ReplayBuffer> buffer;
    { std::scoped_lock lock(component.replay_mutex); buffer = component.replay_buffer; }
    return {component.replay_state.load(), component.replay_error.load(),
            buffer ? buffer->packet_count() : 0,
            buffer ? buffer->ram_bytes() : 0,
            buffer ? buffer->disk_bytes() : 0,
            component.replay_dropped.load(), component.saves_completed.load(),
            component.saves_failed.load(), component.last_save_error.load(),
            component.saves_active.load(), component.last_save_shorter.load(),
            component.replay_encoded_frames.load(),
            component.last_save_duration_ms.load(std::memory_order_relaxed)};
}

extern "C" long __cdecl ClipprGetLastReplayPath(void* capture, wchar_t* path, int32_t capacity) {
    if (!capture || !path || capacity <= 0) return E_INVALIDARG;
    auto& component = *static_cast<CaptureHandle*>(capture)->component;
    std::scoped_lock lock(component.replay_mutex);
    if (component.last_replay_path.empty()) { path[0] = L'\0'; return S_FALSE; }
    if (component.last_replay_path.size() + 1 > static_cast<size_t>(capacity))
        return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    wcscpy_s(path, static_cast<size_t>(capacity), component.last_replay_path.c_str());
    return S_OK;
}

extern "C" long __cdecl ClipprSetReplayEnabled(void* capture, int32_t enabled) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->SetReplayEnabled(enabled != 0);
}

extern "C" long __cdecl ClipprEnumerateAudioDevices(int32_t render_devices,
    ClipprAudioDevice* devices, int32_t capacity, int32_t* count) {
    if (!count || capacity < 0 || (capacity && !devices)) return E_INVALIDARG;
    try {
        const auto found = clippr::capture::AudioCaptureManager::Enumerate(render_devices != 0);
        *count = static_cast<int32_t>(found.size());
        if (capacity < *count) return capacity == 0 ? S_OK : HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
        for (int32_t i = 0; i < *count; ++i) {
            wcsncpy_s(devices[i].id, found[i].id.c_str(), _TRUNCATE);
            wcsncpy_s(devices[i].name, found[i].name.c_str(), _TRUNCATE);
            devices[i].is_default = found[i].is_default ? 1 : 0;
        }
        return S_OK;
    } catch (...) { return E_FAIL; }
}

extern "C" long __cdecl ClipprConfigureAudio(void* capture, int32_t desktop_enabled,
    int32_t microphone_enabled) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureAudio(
        desktop_enabled != 0, microphone_enabled != 0);
}

extern "C" ClipprAudioStatus __cdecl ClipprGetAudioStatus(void* capture) {
    if (!capture) return {0, 0, 0, 0, E_POINTER, E_POINTER, E_POINTER, E_POINTER, 0, E_POINTER};
    const auto status = static_cast<CaptureHandle*>(capture)->component->audio_capture.Status();
    return {status.desktop_running ? 1 : 0, status.microphone_running ? 1 : 0,
            status.game_running ? 1 : 0, status.chat_running ? 1 : 0,
            static_cast<int32_t>(status.desktop_error), static_cast<int32_t>(status.microphone_error),
            static_cast<int32_t>(status.game_error), static_cast<int32_t>(status.chat_error),
            status.music_running ? 1 : 0, static_cast<int32_t>(status.music_error)};
}

extern "C" ClipprAudioLevels __cdecl ClipprTakeAudioLevels(void* capture) {
    if (!capture) return {};
    const auto levels = static_cast<CaptureHandle*>(capture)->component->audio_capture.TakeLevels();
    return {levels[0], levels[1], levels[2], levels[3], levels[4]};
}

extern "C" long __cdecl ClipprSetAudioDevices(void* capture, wchar_t const* render_id,
    wchar_t const* microphone_id) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->SetAudioDevices(render_id, microphone_id);
}

extern "C" long __cdecl ClipprConfigureProcessAudio(void* capture, uint32_t game_process_id,
    uint32_t chat_process_id) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureProcessAudio(game_process_id, chat_process_id);
}

extern "C" long __cdecl ClipprConfigureMusicAudio(void* capture, uint32_t music_process_id) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureMusicAudio(music_process_id);
}

extern "C" long __cdecl ClipprSetAudioGain(void* capture, int32_t stream_kind, float gain) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->audio_capture.SetGain(
        static_cast<clippr::core::StreamKind>(stream_kind), gain);
}

extern "C" long __cdecl ClipprConfigureHotkey(void* capture, uint32_t modifiers, uint32_t virtual_key) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureHotkey(modifiers, virtual_key);
}

extern "C" long __cdecl ClipprRouteReplayHotkeyToApp(void* capture, int32_t enabled) {
    if (!capture) return E_POINTER;
    auto& component = *static_cast<CaptureHandle*>(capture)->component;
    component.replay_hotkey_requests.store(0, std::memory_order_release);
    component.route_replay_hotkey_to_app.store(enabled != 0, std::memory_order_release);
    return S_OK;
}

extern "C" uint64_t __cdecl ClipprTakeReplayHotkeyRequests(void* capture) {
    return capture ? static_cast<CaptureHandle*>(capture)->component->replay_hotkey_requests.exchange(0) : 0;
}

extern "C" long __cdecl ClipprSaveScreenshot(void* capture, const wchar_t* path, int32_t jpeg, uint32_t quality) {
    if (!capture) return E_POINTER;
    try { return static_cast<CaptureHandle*>(capture)->component->SaveScreenshot(path, jpeg != 0, quality); }
    catch (std::bad_alloc const&) { return E_OUTOFMEMORY; }
    catch (...) { return E_FAIL; }
}

extern "C" long __cdecl ClipprConfigureScreenshot(void* capture, const wchar_t* folder, int32_t jpeg,
    uint32_t quality, uint32_t modifiers, uint32_t virtual_key) {
    if (!capture) return E_POINTER;
    return static_cast<CaptureHandle*>(capture)->component->ConfigureScreenshot(
        folder, jpeg != 0, quality, modifiers, virtual_key);
}

extern "C" uint64_t __cdecl ClipprGetScreenshotCount(void* capture) {
    return capture ? static_cast<CaptureHandle*>(capture)->component->screenshots_completed.load() : 0;
}

extern "C" int32_t __cdecl ClipprGetScreenshotError(void* capture) {
    return capture ? static_cast<int32_t>(static_cast<CaptureHandle*>(capture)->component->screenshot_error.load()) : E_POINTER;
}

extern "C" long __cdecl ClipprGetLastScreenshotPath(void* capture, wchar_t* path, int32_t capacity) {
    if (!capture || !path || capacity <= 0) return E_INVALIDARG;
    auto& component = *static_cast<CaptureHandle*>(capture)->component;
    std::scoped_lock lock(component.screenshot_config_mutex);
    if (component.last_screenshot_path.empty()) { path[0] = L'\0'; return S_FALSE; }
    if (component.last_screenshot_path.size() + 1 > static_cast<size_t>(capacity)) return HRESULT_FROM_WIN32(ERROR_INSUFFICIENT_BUFFER);
    wcscpy_s(path, static_cast<size_t>(capacity), component.last_screenshot_path.c_str());
    return S_OK;
}
