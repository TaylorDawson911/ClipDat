#include <Windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>

#include <cstdio>

using Microsoft::WRL::ComPtr;

int wmain(int argc, wchar_t** argv) {
    if (argc != 2) return 2;
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 3;
    hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) { CoUninitialize(); return 4; }
    int result = 1;
    do {
        ComPtr<IMFSourceReader> reader;
        hr = MFCreateSourceReaderFromURL(argv[1], nullptr, reader.GetAddressOf());
        if (FAILED(hr)) break;
        ComPtr<IMFMediaType> native_type;
        hr = reader->GetNativeMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0,
                                        native_type.GetAddressOf());
        if (FAILED(hr)) break;
        GUID subtype{};
        hr = native_type->GetGUID(MF_MT_SUBTYPE, &subtype);
        if (FAILED(hr) || subtype != MFVideoFormat_H264) { hr = MF_E_INVALIDMEDIATYPE; break; }
        UINT32 width{}, height{};
        hr = MFGetAttributeSize(native_type.Get(), MF_MT_FRAME_SIZE, &width, &height);
        if (FAILED(hr)) break;

        ComPtr<IMFMediaType> decoded_type;
        hr = MFCreateMediaType(decoded_type.GetAddressOf());
        if (FAILED(hr)) break;
        hr = decoded_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (FAILED(hr)) break;
        hr = decoded_type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        if (FAILED(hr)) break;
        hr = reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr,
                                          decoded_type.Get());
        if (FAILED(hr)) break;
        unsigned decoded_frames{};
        LONGLONG first_time{-1}, last_time{-1};
        bool reached_end = false;
        for (unsigned i = 0; i < 120'000; ++i) {
            DWORD actual_stream{}, flags{};
            LONGLONG timestamp{};
            ComPtr<IMFSample> sample;
            hr = reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0,
                                    &actual_stream, &flags, &timestamp, sample.GetAddressOf());
            if (FAILED(hr) || (flags & MF_SOURCE_READERF_ERROR)) break;
            if (flags & MF_SOURCE_READERF_ENDOFSTREAM) { reached_end = true; break; }
            if (!sample) continue;
            DWORD bytes{};
            hr = sample->GetTotalLength(&bytes);
            if (FAILED(hr) || !bytes || (last_time >= 0 && timestamp < last_time)) break;
            if (first_time < 0) first_time = timestamp;
            last_time = timestamp;
            ++decoded_frames;
        }
        if (decoded_frames >= 5 && reached_end) {
            wprintf(L"H.264 decoded to end: %u frames, %u x %u, first=%lld, last=%lld, duration=%.2fs\n",
                         decoded_frames, width, height, first_time, last_time,
                         static_cast<double>(last_time - first_time) / 10'000'000.0);
            result = 0;
        } else {
            wprintf(L"Only %u frames decoded\n", decoded_frames);
        }
    } while (false);
    if (result) wprintf(L"Validation failed: 0x%08X\n", static_cast<unsigned>(hr));
    (void)MFShutdown();
    CoUninitialize();
    return result;
}
