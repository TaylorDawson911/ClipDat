#include <Windows.h>
#include <objbase.h>
#include <ShlObj.h>
#include <winrt/base.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Data.Json.h>

#include <array>
#include <cstring>
#include <filesystem>
#include <memory>
#include <string>
#include <string_view>
#include <vector>

namespace
{
    using namespace winrt::Windows::Data::Json;

    constexpr wchar_t defaultJson[] = L"{}";
    constexpr const wchar_t* knownKeys[]{
        L"schemaVersion", L"captureMode", L"preferredDisplay", L"preferredApplicationProcessId",
        L"clipLengthSeconds", L"resolution", L"frameRate", L"encoder", L"qualityPreset",
        L"videoFormat", L"filenameFormat", L"desktopAudioEnabled", L"microphoneAudioEnabled", L"audioOutputId",
        L"audioInputId", L"gameAudioProcessId", L"discordAudioProcessId", L"clipFolder",
        L"hotkey", L"saveNotificationSound", L"startWithWindows", L"startCaptureAutomatically",
        L"replayMemoryLimitMb", L"musicAudioProcessId",
        L"desktopVolume", L"microphoneVolume", L"gameVolume", L"chatVolume", L"musicVolume",
        L"desktopMuted", L"microphoneMuted", L"gameMuted", L"chatMuted", L"musicMuted",
        L"screenshotHotkey", L"screenshotFormat", L"screenshotJpegQuality",
        L"copyScreenshotToClipboard", L"screenshotNotificationSound", L"notificationStyle",
        L"minimizeToTrayOnClose", L"keepCaptureRunningWhileHidden",
        L"minimumFreeDiskSpaceGb", L"voiceCommandsEnabled",
        L"voiceWakePhrase", L"voiceConfidenceThreshold", L"voiceCooldownSeconds",
        L"voiceCommandPhrases", L"advancedModeEnabled", L"showPreviewDiagnostics"
    };

    std::filesystem::path KnownFolderPath(REFKNOWNFOLDERID folder, DWORD flags)
    {
        PWSTR localAppData = nullptr;
        const HRESULT hr = SHGetKnownFolderPath(folder, flags, nullptr, &localAppData);
        if (FAILED(hr)) throw winrt::hresult_error(hr);
        const std::unique_ptr<wchar_t, decltype(&CoTaskMemFree)> ownedPath(localAppData, &CoTaskMemFree);
        return std::filesystem::path(ownedPath.get());
    }

    std::filesystem::path SettingsPath()
    {
        // Windows maps LocalAppData to the app's LocalCache location for packaged apps.
        return KnownFolderPath(FOLDERID_LocalAppData, KF_FLAG_DEFAULT) / L"Clippr" / L"settings.json";
    }

    std::wstring ReadText(const std::filesystem::path& path)
    {
        HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING,
                                  FILE_ATTRIBUTE_NORMAL, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
        LARGE_INTEGER size{};
        if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > 4 * 1024 * 1024)
        {
            const auto error = GetLastError();
            CloseHandle(file);
            throw winrt::hresult_error(HRESULT_FROM_WIN32(error ? error : ERROR_FILE_TOO_LARGE));
        }
        std::string utf8(static_cast<size_t>(size.QuadPart), '\0');
        DWORD read = 0;
        const bool ok = utf8.empty() || ReadFile(file, utf8.data(), static_cast<DWORD>(utf8.size()), &read, nullptr);
        CloseHandle(file);
        if (!ok || read != utf8.size()) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
        const int required = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), static_cast<int>(utf8.size()), nullptr, 0);
        if (required == 0 && !utf8.empty()) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
        std::wstring text(static_cast<size_t>(required), L'\0');
        if (required && MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, utf8.data(), static_cast<int>(utf8.size()), text.data(), required) == 0)
            throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
        return text;
    }

    std::string ToUtf8(std::wstring_view text)
    {
        if (text.empty()) return {};
        const int required = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), nullptr, 0, nullptr, nullptr);
        if (required == 0) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
        std::string result(static_cast<size_t>(required), '\0');
        if (WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, text.data(), static_cast<int>(text.size()), result.data(), required, nullptr, nullptr) == 0)
            throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
        return result;
    }

    void WriteAtomically(const std::filesystem::path& path, std::string_view data)
    {
        std::filesystem::create_directories(path.parent_path());
        const auto tempPath = path.parent_path() / (path.filename().wstring() + L".tmp." + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(GetTickCount64()));
        HANDLE file = CreateFileW(tempPath.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                                  FILE_ATTRIBUTE_NORMAL | FILE_FLAG_WRITE_THROUGH, nullptr);
        if (file == INVALID_HANDLE_VALUE) throw winrt::hresult_error(HRESULT_FROM_WIN32(GetLastError()));
        DWORD written = 0;
        const bool writeOk = data.size() <= MAXDWORD && WriteFile(file, data.data(), static_cast<DWORD>(data.size()), &written, nullptr) && written == data.size();
        const bool flushOk = writeOk && FlushFileBuffers(file);
        const auto writeError = GetLastError();
        CloseHandle(file);
        if (!flushOk)
        {
            DeleteFileW(tempPath.c_str());
            throw winrt::hresult_error(HRESULT_FROM_WIN32(writeError ? writeError : ERROR_WRITE_FAULT));
        }

        BOOL replaced = FALSE;
        if (GetFileAttributesW(path.c_str()) != INVALID_FILE_ATTRIBUTES)
            replaced = ReplaceFileW(path.c_str(), tempPath.c_str(), nullptr, REPLACEFILE_WRITE_THROUGH, nullptr, nullptr);
        else
            replaced = MoveFileExW(tempPath.c_str(), path.c_str(), MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH);
        if (!replaced)
        {
            const auto error = GetLastError();
            DeleteFileW(tempPath.c_str());
            throw winrt::hresult_error(HRESULT_FROM_WIN32(error));
        }
    }

    void CopyStringToCaller(std::wstring_view value, wchar_t** result)
    {
        const size_t bytes = (value.size() + 1) * sizeof(wchar_t);
        auto buffer = static_cast<wchar_t*>(CoTaskMemAlloc(bytes));
        if (!buffer) throw winrt::hresult_error(E_OUTOFMEMORY);
        memcpy(buffer, value.data(), value.size() * sizeof(wchar_t));
        buffer[value.size()] = L'\0';
        *result = buffer;
    }
}

extern "C" __declspec(dllexport) HRESULT __cdecl ClipprLoadSettingsJson(wchar_t** json, int32_t* state)
{
    if (!json || !state) return E_POINTER;
    *json = nullptr;
    *state = 0;
    try
    {
        const auto path = SettingsPath();
        if (!std::filesystem::exists(path))
        {
            CopyStringToCaller(defaultJson, json);
            return S_OK;
        }
        try
        {
            const auto text = ReadText(path);
            const auto parsed = JsonObject::Parse(text);
            CopyStringToCaller(parsed.Stringify().c_str(), json);
            *state = 1;
            return S_OK;
        }
        catch (...)
        {
            *state = 2;
            CopyStringToCaller(defaultJson, json);
            return S_OK;
        }
    }
    catch (const winrt::hresult_error& error) { return error.code(); }
    catch (...) { return E_FAIL; }
}

extern "C" __declspec(dllexport) HRESULT __cdecl ClipprSaveSettingsJson(wchar_t const* json)
{
    if (!json) return E_POINTER;
    try
    {
        const auto requested = JsonObject::Parse(json);
        JsonObject merged = JsonObject();
        const auto path = SettingsPath();
        try
        {
            const auto source = path;
            if (std::filesystem::exists(source)) merged = JsonObject::Parse(ReadText(source));
        }
        catch (...) { merged = JsonObject(); }

        for (const auto key : knownKeys)
        {
            const winrt::hstring propertyName(key);
            if (requested.HasKey(propertyName)) merged.Insert(propertyName, requested.Lookup(propertyName));
        }

        constexpr int currentSchema = 1;
        int schema = currentSchema;
        try
        {
            const auto current = merged.Lookup(winrt::hstring(L"schemaVersion")).GetNumber();
            if (current > currentSchema && current <= 100000) schema = static_cast<int>(current);
        }
        catch (...) {}
        merged.Insert(winrt::hstring(L"schemaVersion"), JsonValue::CreateNumberValue(schema));
        const auto serialized = merged.Stringify();
        WriteAtomically(path, ToUtf8(serialized.c_str()));
        return S_OK;
    }
    catch (const winrt::hresult_error& error) { return error.code(); }
    catch (...) { return E_FAIL; }
}
