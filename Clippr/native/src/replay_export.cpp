#include "clippr/replay/replay_export.hpp"

#include <Windows.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
#include <libavutil/error.h>
#include <libavutil/mem.h>
}

#include <algorithm>
#include <array>
#include <cerrno>
#include <climits>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <span>
#include <string>
#include <vector>

namespace clippr::replay {
namespace {

using namespace clippr::core;

bool IsAudio(StreamKind kind) noexcept { return kind != StreamKind::video; }

struct ExportGuard {
    AVFormatContext* format{};
    std::wstring temporary_path;
    bool published{};
    ~ExportGuard() {
        if (format) {
            if (format->pb && !(format->oformat->flags & AVFMT_NOFILE)) avio_closep(&format->pb);
            avformat_free_context(format);
        }
        if (!published && !temporary_path.empty()) DeleteFileW(temporary_path.c_str());
    }
};

HRESULT HresultFromFileError(DWORD error) noexcept {
    switch (error) {
    case ERROR_DISK_FULL:
    case ERROR_HANDLE_DISK_FULL: return HRESULT_FROM_WIN32(ERROR_DISK_FULL);
    case ERROR_ACCESS_DENIED: return HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
    case ERROR_ALREADY_EXISTS:
    case ERROR_FILE_EXISTS: return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
    case ERROR_PATH_NOT_FOUND: return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
    case ERROR_FILENAME_EXCED_RANGE: return HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE);
    default: return HRESULT_FROM_WIN32(error ? error : ERROR_WRITE_FAULT);
    }
}

HRESULT HresultFromErrorCode(std::error_code const& error) noexcept {
    if (error == std::errc::no_space_on_device) return HRESULT_FROM_WIN32(ERROR_DISK_FULL);
    if (error == std::errc::permission_denied) return HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
    if (error == std::errc::file_exists) return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
    if (error == std::errc::filename_too_long) return HRESULT_FROM_WIN32(ERROR_FILENAME_EXCED_RANGE);
    if (error == std::errc::no_such_file_or_directory || error == std::errc::not_a_directory)
        return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
    return HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
}

HRESULT HresultFromAvError(int error) noexcept {
    if (error == AVERROR(ENOSPC)) return HRESULT_FROM_WIN32(ERROR_DISK_FULL);
    if (error == AVERROR(EACCES) || error == AVERROR(EPERM)) return HRESULT_FROM_WIN32(ERROR_ACCESS_DENIED);
    if (error == AVERROR(ENOENT)) return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
    if (error == AVERROR(EEXIST)) return HRESULT_FROM_WIN32(ERROR_FILE_EXISTS);
    return HRESULT_FROM_WIN32(ERROR_WRITE_FAULT);
}

std::string Utf8FromWide(std::wstring const& value) {
    const int size = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), nullptr, 0, nullptr, nullptr);
    if (size <= 0) throw std::filesystem::filesystem_error("invalid UTF-16 path",
        std::error_code(static_cast<int>(GetLastError()), std::system_category()));
    std::string result(static_cast<std::size_t>(size), '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, value.data(),
        static_cast<int>(value.size()), result.data(), size, nullptr, nullptr))
        throw std::filesystem::filesystem_error("cannot encode path",
            std::error_code(static_cast<int>(GetLastError()), std::system_category()));
    return result;
}

std::vector<std::span<const std::byte>> AnnexBNals(std::span<const std::byte> data) {
    std::vector<std::span<const std::byte>> result;
    auto find_start = [&](std::size_t from) -> std::pair<std::size_t, std::size_t> {
        for (auto i = from; i + 3 <= data.size(); ++i) {
            if (data[i] != std::byte{0} || data[i + 1] != std::byte{0}) continue;
            if (data[i + 2] == std::byte{1}) return {i, 3};
            if (i + 4 <= data.size() && data[i + 2] == std::byte{0} && data[i + 3] == std::byte{1})
                return {i, 4};
        }
        return {data.size(), 0};
    };
    auto [start, prefix] = find_start(0);
    while (prefix) {
        const auto begin = start + prefix;
        auto [next, next_prefix] = find_start(begin);
        auto end = next;
        while (end > begin && data[end - 1] == std::byte{0}) --end;
        if (end > begin) result.emplace_back(data.data() + begin, end - begin);
        start = next;
        prefix = next_prefix;
    }
    return result;
}

bool IsAnnexB(std::span<const std::byte> data) noexcept {
    return data.size() >= 3 && data[0] == std::byte{0} && data[1] == std::byte{0} &&
        (data[2] == std::byte{1} || (data.size() >= 4 && data[2] == std::byte{0} && data[3] == std::byte{1}));
}

std::vector<std::byte> MakeAvcConfiguration(ReplaySnapshot const& snapshot) {
    std::vector<std::span<const std::byte>> sps, pps;
    for (auto const& ref : snapshot.packets) {
        if (ref.stream != StreamKind::video) continue;
        auto packet = ref.read();
        if (!packet) continue;
        for (auto nal : AnnexBNals(packet->bytes)) {
            const auto type = std::to_integer<unsigned char>(nal[0]) & 0x1f;
            if (type == 7) sps.push_back(nal);
            else if (type == 8) pps.push_back(nal);
        }
        if ((sps.empty() || pps.empty()) && ref.codec_extradata && IsAnnexB(*ref.codec_extradata)) {
            for (auto nal : AnnexBNals(*ref.codec_extradata)) {
                const auto type = std::to_integer<unsigned char>(nal[0]) & 0x1f;
                if (type == 7) sps.push_back(nal);
                else if (type == 8) pps.push_back(nal);
            }
        }
        if (!sps.empty() && !pps.empty()) break;
    }
    if (sps.empty() || pps.empty() || sps.front().size() < 4) return {};
    if (sps.size() > 31 || pps.size() > 255) return {};
    std::vector<std::byte> result{std::byte{1}, sps.front()[1], sps.front()[2], sps.front()[3],
                                  std::byte{0xff}, static_cast<std::byte>(0xe0 | sps.size())};
    auto append_nals = [&result](auto const& nals) {
        for (auto nal : nals) {
            if (nal.empty() || nal.size() > 65535) continue;
            result.push_back(static_cast<std::byte>((nal.size() >> 8) & 0xff));
            result.push_back(static_cast<std::byte>(nal.size() & 0xff));
            result.insert(result.end(), nal.begin(), nal.end());
        }
    };
    append_nals(sps);
    result.push_back(static_cast<std::byte>(pps.size()));
    append_nals(pps);
    return result;
}

std::vector<std::byte> ToAvcc(std::span<const std::byte> source) {
    if (!IsAnnexB(source)) return {source.begin(), source.end()};
    const auto nals = AnnexBNals(source);
    std::vector<std::byte> result;
    result.reserve(source.size() + nals.size() * 2);
    for (auto nal : nals) {
        if (nal.empty() || nal.size() > UINT32_MAX) continue;
        const auto length = static_cast<std::uint32_t>(nal.size());
        result.push_back(static_cast<std::byte>(length >> 24));
        result.push_back(static_cast<std::byte>(length >> 16));
        result.push_back(static_cast<std::byte>(length >> 8));
        result.push_back(static_cast<std::byte>(length));
        result.insert(result.end(), nal.begin(), nal.end());
    }
    return result;
}

int CopyExtraData(AVCodecParameters* parameters, std::span<const std::byte> data) {
    if (data.empty()) return AVERROR_INVALIDDATA;
    auto* extra = static_cast<std::uint8_t*>(av_mallocz(data.size() + AV_INPUT_BUFFER_PADDING_SIZE));
    if (!extra) return AVERROR(ENOMEM);
    std::memcpy(extra, data.data(), data.size());
    parameters->extradata = extra;
    parameters->extradata_size = static_cast<int>(data.size());
    return 0;
}

int WritePacket(AVFormatContext* format, AVStream* stream, ReplayPacketRef const& ref,
                ReplaySnapshot const& snapshot, bool convert_h264) {
    auto packet_data = ref.read();
    if (!packet_data) return AVERROR(EIO);
    std::vector<std::byte> converted;
    std::span<const std::byte> bytes(packet_data->bytes);
    if (convert_h264) {
        converted = ToAvcc(bytes);
        bytes = converted;
    }
    if (bytes.empty() || bytes.size() > INT_MAX) return AVERROR_INVALIDDATA;
    AVPacket* packet = av_packet_alloc();
    if (!packet) return AVERROR(ENOMEM);
    int result = av_new_packet(packet, static_cast<int>(bytes.size()));
    if (result >= 0) {
        std::memcpy(packet->data, bytes.data(), bytes.size());
        const auto pts = ref.timestamp.count() - snapshot.start.count();
        const auto dts = ref.decode_timestamp
            ? ref.decode_timestamp->count() - snapshot.start.count() : pts;
        packet->pts = pts;
        packet->dts = dts;
        packet->duration = std::max<int64_t>(1, ref.duration.count());
        packet->stream_index = stream->index;
        packet->pos = -1;
        if (ref.keyframe) packet->flags |= AV_PKT_FLAG_KEY;
        av_packet_rescale_ts(packet, AVRational{1, 10'000'000}, stream->time_base);
        result = av_interleaved_write_frame(format, packet);
    }
    av_packet_free(&packet);
    return result;
}

HRESULT NewTemporaryFile(std::wstring const& final_path, std::wstring& temporary_path) {
    const std::filesystem::path target(final_path);
    std::error_code ec;
    auto parent = target.parent_path();
    if (parent.empty()) parent = std::filesystem::current_path(ec);
    if (ec) return HRESULT_FROM_WIN32(ERROR_PATH_NOT_FOUND);
    std::filesystem::create_directories(parent, ec);
    if (ec) return HresultFromErrorCode(ec);
    GUID guid{};
    if (FAILED(CoCreateGuid(&guid))) return E_FAIL;
    wchar_t id[40]{};
    if (!StringFromGUID2(guid, id, static_cast<int>(std::size(id)))) return E_FAIL;
    std::wstring clean_id(id);
    clean_id.erase(std::remove(clean_id.begin(), clean_id.end(), L'{'), clean_id.end());
    clean_id.erase(std::remove(clean_id.begin(), clean_id.end(), L'}'), clean_id.end());
    const auto extension = target.extension().wstring();
    temporary_path = (parent / (L".clippr-" + clean_id + L".partial" + extension)).wstring();
    HANDLE file = CreateFileW(temporary_path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_NEW,
                              FILE_ATTRIBUTE_TEMPORARY, nullptr);
    if (file == INVALID_HANDLE_VALUE) return HresultFromFileError(GetLastError());
    CloseHandle(file);
    return S_OK;
}

} // namespace

long ExportReplayFile(ReplaySnapshot const& snapshot,
                      clippr::encode::VideoEncodingConfig video,
                      std::wstring const& path) noexcept {
    if (snapshot.packets.empty() || path.empty() || !video.width || !video.height || !video.fps)
        return E_INVALIDARG;
    try {
        const std::filesystem::path final_path(path);
        const auto extension = final_path.extension().wstring();
        const bool mp4 = _wcsicmp(extension.c_str(), L".mp4") == 0;
        const bool mkv = _wcsicmp(extension.c_str(), L".mkv") == 0;
        if (!mp4 && !mkv) return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);

        const auto video_ref = std::find_if(snapshot.packets.begin(), snapshot.packets.end(),
            [](auto const& packet) { return packet.stream == StreamKind::video && packet.keyframe; });
        if (video_ref == snapshot.packets.end()) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        const auto has_audio = std::any_of(snapshot.packets.begin(), snapshot.packets.end(),
            [](auto const& packet) { return IsAudio(packet.stream); });

        ExportGuard guard;
        HRESULT hr = NewTemporaryFile(path, guard.temporary_path);
        if (FAILED(hr)) return hr;
        const auto format_name = mp4 ? "mp4" : "matroska";
        int result = avformat_alloc_output_context2(&guard.format, nullptr, format_name, nullptr);
        if (result < 0 || !guard.format) return HresultFromAvError(result < 0 ? result : AVERROR(ENOMEM));

        AVStream* video_stream = avformat_new_stream(guard.format, nullptr);
        if (!video_stream) return E_OUTOFMEMORY;
        video_stream->time_base = AVRational{1, 10'000'000};
        auto* video_parameters = video_stream->codecpar;
        video_parameters->codec_type = AVMEDIA_TYPE_VIDEO;
        video_parameters->codec_id = AV_CODEC_ID_H264;
        video_parameters->width = static_cast<int>(video.width);
        video_parameters->height = static_cast<int>(video.height);
        video_parameters->bit_rate = video.bitrate;
        video_stream->avg_frame_rate = AVRational{static_cast<int>(video.fps), 1};
        const auto avcc = MakeAvcConfiguration(snapshot);
        if ((result = CopyExtraData(video_parameters, avcc)) < 0) return HresultFromAvError(result);

        std::vector<std::pair<StreamKind, AVStream*>> audio_streams;
        for (const auto kind : {StreamKind::desktop_audio, StreamKind::microphone_audio,
                                StreamKind::game_audio, StreamKind::chat_audio, StreamKind::music_audio}) {
            const auto audio_ref = std::find_if(snapshot.packets.begin(), snapshot.packets.end(),
                [kind](auto const& packet) { return packet.stream == kind; });
            if (audio_ref == snapshot.packets.end()) continue;
            const bool audio_aac = audio_ref->audio_codec == AudioCodec::aac;
            const bool audio_opus_matroska = audio_ref->audio_codec == AudioCodec::opus && mkv;
            if ((!audio_aac && !audio_opus_matroska) || !audio_ref->sample_rate || !audio_ref->channels ||
                !audio_ref->codec_extradata || !audio_ref->codec_extradata->size())
                return HRESULT_FROM_WIN32(ERROR_NOT_SUPPORTED);
            AVStream* stream = avformat_new_stream(guard.format, nullptr);
            if (!stream) return E_OUTOFMEMORY;
            stream->time_base = AVRational{1, 10'000'000};
            auto* parameters = stream->codecpar;
            parameters->codec_type = AVMEDIA_TYPE_AUDIO;
            parameters->codec_id = audio_aac ? AV_CODEC_ID_AAC : AV_CODEC_ID_OPUS;
            parameters->sample_rate = static_cast<int>(audio_ref->sample_rate);
            if (audio_aac) parameters->bit_rate = 128'000;
            av_channel_layout_default(&parameters->ch_layout, audio_ref->channels);
            if ((result = CopyExtraData(parameters, *audio_ref->codec_extradata)) < 0)
                return HresultFromAvError(result);
            AVDictionary* metadata{};
            const char* track_name = kind == StreamKind::desktop_audio ? "Desktop audio" :
                kind == StreamKind::microphone_audio ? "Microphone" :
                kind == StreamKind::game_audio ? "Game audio" :
                kind == StreamKind::music_audio ? "Music audio" : "Application audio";
            av_dict_set(&metadata, "title", track_name, 0);
            stream->metadata = metadata;
            audio_streams.emplace_back(kind, stream);
        }

        if (!(guard.format->oformat->flags & AVFMT_NOFILE)) {
            const auto narrow_temp_path = Utf8FromWide(guard.temporary_path);
            result = avio_open(&guard.format->pb, narrow_temp_path.c_str(), AVIO_FLAG_WRITE);
            if (result < 0) return HresultFromAvError(result);
        }
        result = avformat_write_header(guard.format, nullptr);
        if (result < 0) return HresultFromAvError(result);

        std::vector<ReplayPacketRef const*> ordered;
        ordered.reserve(snapshot.packets.size());
        for (auto const& packet : snapshot.packets) {
            if ((packet.stream == StreamKind::video && packet.timestamp >= snapshot.start) ||
                (IsAudio(packet.stream) && has_audio && packet.timestamp >= snapshot.start))
                ordered.push_back(&packet);
        }
        std::stable_sort(ordered.begin(), ordered.end(), [](auto lhs, auto rhs) {
            const auto left_dts = lhs->decode_timestamp.value_or(lhs->timestamp);
            const auto right_dts = rhs->decode_timestamp.value_or(rhs->timestamp);
            return left_dts < right_dts;
        });
        std::size_t written{};
        for (auto const* packet : ordered) {
            AVStream* stream = video_stream;
            if (IsAudio(packet->stream)) {
                const auto found = std::find_if(audio_streams.begin(), audio_streams.end(),
                    [packet](auto const& entry) { return entry.first == packet->stream; });
                stream = found == audio_streams.end() ? nullptr : found->second;
            }
            if (!stream) continue;
            result = WritePacket(guard.format, stream, *packet, snapshot,
                                 packet->stream == StreamKind::video);
            if (result < 0) return HresultFromAvError(result);
            ++written;
        }
        if (!written) return HRESULT_FROM_WIN32(ERROR_INVALID_DATA);
        result = av_write_trailer(guard.format);
        if (result < 0) return HresultFromAvError(result);
        if (guard.format->pb && !(guard.format->oformat->flags & AVFMT_NOFILE)) {
            result = avio_closep(&guard.format->pb);
            if (result < 0) return HresultFromAvError(result);
        }
        if (!MoveFileExW(guard.temporary_path.c_str(), path.c_str(), MOVEFILE_WRITE_THROUGH))
            return HresultFromFileError(GetLastError());
        guard.published = true;
        return S_OK;
    } catch (std::bad_alloc const&) {
        return E_OUTOFMEMORY;
    } catch (std::filesystem::filesystem_error const& error) {
        return HresultFromFileError(error.code() == std::errc::permission_denied
            ? ERROR_ACCESS_DENIED : ERROR_PATH_NOT_FOUND);
    } catch (...) {
        return E_FAIL;
    }
}

} // namespace clippr::replay
