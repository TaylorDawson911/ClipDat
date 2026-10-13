#include <Windows.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavformat/avformat.h>
}

#include <algorithm>
#include <cmath>
#include <cstdio>
#include <limits>
#include <vector>

namespace {

struct Decoder {
    AVCodecContext* context{};
    ~Decoder() { avcodec_free_context(&context); }
};

bool DecodeFile(const char* path) {
    AVFormatContext* format{};
    int result = avformat_open_input(&format, path, nullptr, nullptr);
    if (result < 0) { std::printf("open failed: %d\n", result); return false; }
    struct FormatGuard { AVFormatContext** value; ~FormatGuard() { avformat_close_input(value); } } format_guard{&format};
    result = avformat_find_stream_info(format, nullptr);
    if (result < 0) { std::printf("stream info failed: %d\n", result); return false; }
    const int video_index = av_find_best_stream(format, AVMEDIA_TYPE_VIDEO, -1, -1, nullptr, 0);
    if (video_index < 0) { std::puts("no video stream"); return false; }
    const int audio_index = av_find_best_stream(format, AVMEDIA_TYPE_AUDIO, -1, video_index, nullptr, 0);
    const double duration_seconds = format->duration == AV_NOPTS_VALUE
        ? 0.0 : static_cast<double>(format->duration) / AV_TIME_BASE;
    if (duration_seconds <= 0.05) { std::puts("duration missing or too short"); return false; }

    std::vector<Decoder> decoders(format->nb_streams);
    for (unsigned i = 0; i < format->nb_streams; ++i) {
        auto* stream = format->streams[i];
        const AVCodec* codec = avcodec_find_decoder(stream->codecpar->codec_id);
        if (!codec) continue;
        auto& decoder = decoders[i];
        decoder.context = avcodec_alloc_context3(codec);
        if (!decoder.context || avcodec_parameters_to_context(decoder.context, stream->codecpar) < 0 ||
            avcodec_open2(decoder.context, codec, nullptr) < 0) {
            avcodec_free_context(&decoder.context);
        }
    }
    if (!decoders[video_index].context) { std::puts("video decoder failed to initialize"); return false; }

    auto* packet = av_packet_alloc();
    auto* frame = av_frame_alloc();
    if (!packet || !frame) { av_packet_free(&packet); av_frame_free(&frame); return false; }
    std::uint64_t video_frames{}, audio_frames{}, video_packets{}, audio_packets{}, frames_after_seek{}, audio_after_seek{};
    std::int64_t first_video{AV_NOPTS_VALUE}, last_video{AV_NOPTS_VALUE};
    std::int64_t first_audio{AV_NOPTS_VALUE}, last_audio{AV_NOPTS_VALUE};
    bool did_seek{};
    bool decode_valid = true;
    auto drain = [&](int stream_index, bool after_seek) {
        auto* decoder = decoders[stream_index].context;
        if (!decoder) return true;
        for (;;) {
            const int receive = avcodec_receive_frame(decoder, frame);
            if (receive == AVERROR(EAGAIN) || receive == AVERROR_EOF) return true;
            if (receive < 0) {
                char error_text[AV_ERROR_MAX_STRING_SIZE]{};
                av_strerror(receive, error_text, sizeof(error_text));
                std::printf("decoder receive failed on stream %d: %s\n", stream_index, error_text);
                return false;
            }
            auto* stream = format->streams[stream_index];
            const auto time = frame->best_effort_timestamp;
            if (stream_index == video_index) {
                if (first_video == AV_NOPTS_VALUE && time != AV_NOPTS_VALUE) first_video = time;
                if (last_video != AV_NOPTS_VALUE && time != AV_NOPTS_VALUE && time < last_video) {
                    av_frame_unref(frame); return false;
                }
                if (time != AV_NOPTS_VALUE) last_video = time;
                ++video_frames;
                if (after_seek) ++frames_after_seek;
            } else if (stream_index == audio_index) {
                if (first_audio == AV_NOPTS_VALUE && time != AV_NOPTS_VALUE) first_audio = time;
                if (time != AV_NOPTS_VALUE) last_audio = time;
                ++audio_frames;
                if (after_seek) ++audio_after_seek;
            }
            (void)stream;
            av_frame_unref(frame);
        }
    };
    while ((result = av_read_frame(format, packet)) >= 0) {
        if (!did_seek && packet->stream_index == video_index &&
            packet->pts != AV_NOPTS_VALUE &&
            packet->pts * av_q2d(format->streams[video_index]->time_base) >= duration_seconds * 0.5) {
            const auto seek_ts = static_cast<std::int64_t>(duration_seconds * 0.5 /
                av_q2d(format->streams[video_index]->time_base));
            if (av_seek_frame(format, video_index, seek_ts, AVSEEK_FLAG_BACKWARD) < 0) {
                av_packet_unref(packet); break;
            }
            avcodec_flush_buffers(decoders[video_index].context);
            if (audio_index >= 0 && decoders[audio_index].context)
                avcodec_flush_buffers(decoders[audio_index].context);
            last_video = AV_NOPTS_VALUE;
            last_audio = AV_NOPTS_VALUE;
            did_seek = true;
            av_packet_unref(packet);
            continue;
        }
        const int index = packet->stream_index;
        if (index == video_index) ++video_packets;
        if (index == audio_index) ++audio_packets;
        if (decoders[index].context) {
            result = avcodec_send_packet(decoders[index].context, packet);
            if (result >= 0 || result == AVERROR(EAGAIN))
            decode_valid = drain(index, did_seek) && decode_valid;
        }
        av_packet_unref(packet);
    }
    if (!did_seek) {
        const auto seek_ts = static_cast<std::int64_t>(duration_seconds * 0.5 /
            av_q2d(format->streams[video_index]->time_base));
        if (av_seek_frame(format, video_index, seek_ts, AVSEEK_FLAG_BACKWARD) >= 0) {
            avcodec_flush_buffers(decoders[video_index].context);
            last_video = AV_NOPTS_VALUE;
            last_audio = AV_NOPTS_VALUE;
            did_seek = true;
            while (av_read_frame(format, packet) >= 0 && frames_after_seek == 0) {
                if (packet->stream_index == video_index && decoders[video_index].context &&
                    avcodec_send_packet(decoders[video_index].context, packet) >= 0)
                    decode_valid = drain(video_index, true) && decode_valid;
                av_packet_unref(packet);
            }
        }
    }
    for (unsigned i = 0; i < decoders.size(); ++i) {
        if (!decoders[i].context) continue;
        const int flush = avcodec_send_packet(decoders[i].context, nullptr);
        if (flush >= 0 || flush == AVERROR_EOF)
            decode_valid = drain(static_cast<int>(i), did_seek) && decode_valid;
        else {
            char error_text[AV_ERROR_MAX_STRING_SIZE]{};
            av_strerror(flush, error_text, sizeof(error_text));
            std::printf("decoder flush failed on stream %u: %s\n", i, error_text);
            decode_valid = false;
        }
    }
    av_packet_free(&packet);
    av_frame_free(&frame);

    const auto start_delta_seconds = audio_index < 0 ? 0.0 :
        std::abs(first_video == AV_NOPTS_VALUE || first_audio == AV_NOPTS_VALUE ? 999.0 :
            first_video * av_q2d(format->streams[video_index]->time_base) -
            first_audio * av_q2d(format->streams[audio_index]->time_base));
    const auto end_delta_seconds = audio_index < 0 ? 0.0 :
        std::abs(last_video == AV_NOPTS_VALUE || last_audio == AV_NOPTS_VALUE ? 999.0 :
            last_video * av_q2d(format->streams[video_index]->time_base) -
            last_audio * av_q2d(format->streams[audio_index]->time_base));
    std::printf("container=%s duration=%.3fs video=%s %ux%u packets=%llu decoded=%llu seekDecoded=%llu audio=%s packets=%llu decoded=%llu avStartDelta=%.3f avEndDelta=%.3f decodeValid=%d sought=%d\n",
        format->iformat->name, duration_seconds,
        avcodec_get_name(format->streams[video_index]->codecpar->codec_id),
        format->streams[video_index]->codecpar->width, format->streams[video_index]->codecpar->height,
        static_cast<unsigned long long>(video_packets), static_cast<unsigned long long>(video_frames),
        static_cast<unsigned long long>(frames_after_seek),
        audio_index < 0 ? "none" : avcodec_get_name(format->streams[audio_index]->codecpar->codec_id),
        static_cast<unsigned long long>(audio_packets), static_cast<unsigned long long>(audio_frames),
        start_delta_seconds, end_delta_seconds, decode_valid ? 1 : 0, did_seek ? 1 : 0);
    return decode_valid && video_packets > 0 && video_frames > 0 && did_seek && frames_after_seek > 0 &&
           (audio_index < 0 || (audio_packets > 0 && audio_frames > 0 && audio_after_seek > 0 &&
                                start_delta_seconds < 0.1 && end_delta_seconds < 0.1));
}

} // namespace

int wmain(int argc, wchar_t** argv) {
    if (argc < 2) { std::puts("usage: verify_replay file.mp4 [file.mkv ...]"); return 2; }
    bool all_valid = true;
    for (int i = 1; i < argc; ++i) {
        const int bytes = WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, nullptr, 0, nullptr, nullptr);
        if (bytes <= 0) { all_valid = false; continue; }
        std::vector<char> path(static_cast<std::size_t>(bytes));
        WideCharToMultiByte(CP_UTF8, 0, argv[i], -1, path.data(), bytes, nullptr, nullptr);
        all_valid &= DecodeFile(path.data());
    }
    return all_valid ? 0 : 1;
}
