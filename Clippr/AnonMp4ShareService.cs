using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Clippr;

public sealed record ShareUpload(string FilePath, long FileSize, long ModifiedUtcTicks, string VideoId,
    string WatchUrl, string? EmbedUrl, string? ThumbnailUrl, DateTimeOffset UploadedAt, string Status,
    string? ProtectedDeleteUrl);

public sealed class AnonMp4ShareService
{
    public const long MaximumBytes = 20L * 1024 * 1024 * 1024;
    private static readonly Uri UploadUri = new("https://anonmp4api.xyz/upload");
    private static readonly HttpClient Client = new(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromMinutes(30) };
    private static readonly SemaphoreSlim UploadGate = new(1, 1);
    private readonly HttpClient _client;
    public AnonMp4ShareService(HttpClient? client = null) => _client = client ?? Client;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv" };

    public async Task<ShareUpload> UploadAsync(string path, IProgress<(long Sent, long Total)>? progress,
        CancellationToken cancellationToken)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists) throw new FileNotFoundException("The selected clip no longer exists.", path);
        if (!Extensions.Contains(file.Extension)) throw new InvalidOperationException("AnonMP4 accepts video clips only.");
        if (file.Length <= 0 || file.Length > MaximumBytes)
            throw new InvalidOperationException("The clip must be between 1 byte and 20 GB.");
        await UploadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read,
                1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var signature = new byte[16];
            var signatureLength = await stream.ReadAsync(signature, cancellationToken).ConfigureAwait(false);
            stream.Position = 0;
            if (!ValidSignature(file.Extension, signature.AsSpan(0, signatureLength)))
                throw new InvalidDataException("The selected file is not a valid video container.");
            using var multipart = new MultipartFormDataContent();
            using var body = new ProgressStreamContent(stream, file.Length, progress);
            body.Headers.ContentType = new MediaTypeHeaderValue(file.Extension.ToLowerInvariant() switch
            {
                ".mp4" => "video/mp4", ".mkv" => "video/x-matroska", ".mov" => "video/quicktime",
                ".avi" => "video/x-msvideo", ".webm" => "video/webm", ".wmv" => "video/x-ms-wmv",
                _ => "video/x-flv"
            });
            multipart.Add(body, "file", file.Name);
            using var request = new HttpRequestMessage(HttpMethod.Post, UploadUri) { Content = multipart };
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw UploadError(response.StatusCode);
            await using var reply = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var json = await JsonDocument.ParseAsync(reply, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = json.RootElement;
            if (!IsSuccess(root)) throw new InvalidDataException("AnonMP4 did not accept the upload.");
            var id = String(root, "video_id");
            var url = String(root, "watch_url");
            if (!ValidId(id) || !ValidUrl(url, "/v/" + id))
                throw new InvalidDataException("AnonMP4 returned an invalid video ID or watch URL.");
            var delete = String(root, "delete_url");
            return new ShareUpload(file.FullName, file.Length, file.LastWriteTimeUtc.Ticks, id!, url!,
                OptionalUrl(root, "embed_url", false), OptionalUrl(root, "thumbnail", true),
                DateTimeOffset.UtcNow, "processing", Protect(delete));
        }
        finally { UploadGate.Release(); }
    }

    public async Task<string> GetStatusAsync(string videoId, CancellationToken token)
    {
        if (!ValidId(videoId)) throw new ArgumentException("Invalid AnonMP4 video ID.", nameof(videoId));
        using var response = await _client.GetAsync(new Uri("https://anonmp4api.xyz/info/" + videoId), token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw UploadError(response.StatusCode);
        await using var reply = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var json = await JsonDocument.ParseAsync(reply, cancellationToken: token).ConfigureAwait(false);
        var root = json.RootElement;
        if (!IsSuccess(root)) return "processing";
        return String(root, "status") switch { "active" => "ready", "failed" => "failed", _ => "processing" };
    }

    private static bool IsSuccess(JsonElement root) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty("success", out var value) && value.ValueKind == JsonValueKind.True;
    private static string? String(JsonElement root, string key) => root.ValueKind == JsonValueKind.Object &&
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool ValidId(string? id) => id is { Length: > 0 and <= 80 } && id.All(char.IsLetterOrDigit);
    private static bool ValidSignature(string extension, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 12) return false;
        return extension.ToLowerInvariant() switch
        {
            ".mp4" or ".mov" => bytes.Slice(4, 4).SequenceEqual("ftyp"u8),
            ".mkv" or ".webm" => bytes.Slice(0, 4).SequenceEqual(new byte[] { 0x1A, 0x45, 0xDF, 0xA3 }),
            ".avi" => bytes.Slice(0, 4).SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("AVI "u8),
            ".flv" => bytes.Slice(0, 3).SequenceEqual("FLV"u8),
            ".wmv" => bytes.Slice(0, 4).SequenceEqual(new byte[] { 0x30, 0x26, 0xB2, 0x75 }),
            _ => false
        };
    }
    private static bool TrustedHost(string host) => host.Equals("anonmp4.to", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("anonmp4.art", StringComparison.OrdinalIgnoreCase);
    private static bool ValidUrl(string? value, string path) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
        TrustedHost(uri.Host) && uri.IsDefaultPort &&
        uri.AbsolutePath.Equals(path, StringComparison.Ordinal) && string.IsNullOrEmpty(uri.Query) &&
        string.IsNullOrEmpty(uri.Fragment) && string.IsNullOrEmpty(uri.UserInfo);
    public static bool IsValidWatchUrl(string? value, string? videoId) =>
        ValidId(videoId) && ValidUrl(value, "/v/" + videoId);
    private static string? OptionalUrl(JsonElement root, string key, bool thumbnail)
    {
        var value = String(root, key);
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps &&
               uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) &&
               (TrustedHost(uri.Host) || (thumbnail &&
                (uri.Host.Equals("cdn.anonmp4.to", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host.Equals("cdn.anonmp4.art", StringComparison.OrdinalIgnoreCase)))) ? value : null;
    }
    private static Exception UploadError(HttpStatusCode status) => new HttpRequestException(status switch
    {
        HttpStatusCode.BadRequest => "AnonMP4 rejected this clip.",
        HttpStatusCode.RequestEntityTooLarge => "This clip is too large for AnonMP4.",
        HttpStatusCode.UnsupportedMediaType => "AnonMP4 does not support this video format.",
        HttpStatusCode.TooManyRequests => "AnonMP4 is rate limiting uploads. Try again later.",
        _ => $"AnonMP4 returned HTTP {(int)status}. Try again later."
    }, null, status);
    private static string? Protect(string? url) => string.IsNullOrEmpty(url) ||
        !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
        !TrustedHost(uri.Host) ? null :
        Convert.ToBase64String(ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(url), null, DataProtectionScope.CurrentUser));

    private sealed class ProgressStreamContent(Stream source, long length, IProgress<(long Sent, long Total)>? progress)
        : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context)
            => await CopyAsync(target, CancellationToken.None).ConfigureAwait(false);
        protected override async Task SerializeToStreamAsync(Stream target, TransportContext? context, CancellationToken token)
            => await CopyAsync(target, token).ConfigureAwait(false);
        private async Task CopyAsync(Stream target, CancellationToken token)
        {
            var buffer = new byte[128 * 1024];
            long sent = 0;
            int count;
            while ((count = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                sent += count;
                progress?.Report((sent, length));
            }
        }
        protected override bool TryComputeLength(out long value) { value = length; return true; }
    }
}
