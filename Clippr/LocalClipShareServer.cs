using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Clippr;

/// <summary>
/// Temporary, same-PC clip delivery for browser preview. The listener is deliberately
/// loopback-only; it is never a public sharing endpoint.
/// </summary>
internal sealed class LocalClipShareServer : IAsyncDisposable
{
    internal sealed record ShareLink(string Url, DateTimeOffset ExpiresAt, string Token);
    private sealed record SharedFile(string Path, string Root, DateTimeOffset ExpiresAt, CancellationTokenSource Revoked);

    private readonly object _gate = new();
    private readonly Dictionary<string, SharedFile> _files = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _connections = new(2, 2);
    private readonly ConcurrentDictionary<int, TcpClient> _clients = new();
    private readonly ConcurrentDictionary<int, Task> _handlers = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly string? _publicHostname;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private bool _disposed;
    private int _nextClientId;
    private const int MaxLinks = 3;
    private const int HeaderLimit = 8192;
    private const int ChunkBytes = 64 * 1024;

    public LocalClipShareServer(string? publicHostname = null)
    {
        _publicHostname = publicHostname;
    }

    public ShareLink CreateLink(string clipFolder, string filePath, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lifetime), "Local links can last up to one hour.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(clipFolder));
        var fullPath = Path.GetFullPath(filePath);
        ValidateSharedFile(root, fullPath);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ExpireLinksLocked();
            if (_files.Count >= MaxLinks) throw new InvalidOperationException("Revoke an existing local link before creating another.");
            EnsureListenerLocked();
            var token = Base64Url(RandomNumberGenerator.GetBytes(32));
            var expiresAt = DateTimeOffset.UtcNow.Add(lifetime);
            _files.Add(token, new SharedFile(fullPath, root, expiresAt, new CancellationTokenSource()));
            var port = ((IPEndPoint)_listener!.LocalEndpoint).Port;
            return new ShareLink($"http://127.0.0.1:{port}/share/{token}", expiresAt, token);
        }
    }

    public bool Revoke(string token)
    {
        lock (_gate)
        {
            if (!_files.Remove(token, out var shared)) return false;
            shared.Revoked.Cancel();
            return true;
        }
    }

    private static void ValidateSharedFile(string root, string path)
    {
        if (!string.Equals(Path.GetDirectoryName(path), root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Only clips directly inside the configured clips folder can be shared.");
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Only MP4 and MKV clips can be shared.", nameof(path));
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new DirectoryNotFoundException("The configured clips folder is unavailable or redirects elsewhere.");
        if (!File.Exists(path)) throw new FileNotFoundException("The saved clip is no longer available.", path);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new UnauthorizedAccessException("Linked files cannot be shared.");
        if (new FileInfo(path).Length == 0) throw new IOException("An empty clip cannot be shared.");
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var shared in _files.Values) shared.Revoked.Cancel();
            _files.Clear();
            _stop.Cancel();
            _listener?.Stop();
            foreach (var client in _clients.Values) client.Dispose();
        }
        if (_acceptTask is not null)
        {
            try { await _acceptTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }
        try { await Task.WhenAll(_handlers.Values).ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        _stop.Dispose();
        _connections.Dispose();
    }

    private void EnsureListenerLocked()
    {
        if (_listener is not null) return;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(4);
        _acceptTask = AcceptLoopAsync(_listener, _stop.Token);
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) when (cancellationToken.IsCancellationRequested) { break; }

            var id = Interlocked.Increment(ref _nextClientId);
            _clients[id] = client;
            var handler = Task.Run(() => HandleClientAsync(client, cancellationToken));
            _handlers[id] = handler;
            _ = handler.ContinueWith(completed =>
            {
                _clients.TryRemove(id, out _);
                _handlers.TryRemove(id, out _);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverToken)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            var token = timeout.Token;
            var entered = false;
            try
            {
                if (!await _connections.WaitAsync(0, token).ConfigureAwait(false))
                {
                    await WriteErrorAsync(client.GetStream(), 503, "Service Unavailable", token).ConfigureAwait(false);
                    return;
                }
                entered = true;
                client.NoDelay = true;
                var stream = client.GetStream();
                var request = await ReadHeadersAsync(stream, token).ConfigureAwait(false);
                if (request is null) return;
                timeout.CancelAfter(Timeout.InfiniteTimeSpan);
                var port = ((IPEndPoint)client.Client.LocalEndPoint!).Port;
                var parsed = ParseRequest(request, _publicHostname, port);
                if (parsed.ErrorStatus != 0)
                {
                    await WriteErrorAsync(stream, parsed.ErrorStatus, parsed.ErrorReason, token).ConfigureAwait(false);
                    return;
                }
                SharedFile? shared;
                lock (_gate)
                {
                    ExpireLinksLocked();
                    _files.TryGetValue(parsed.Token!, out shared);
                }
                if (shared is null)
                {
                    await WriteErrorAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                    return;
                }
                if (!File.Exists(shared.Path))
                {
                    Revoke(parsed.Token!);
                    await WriteErrorAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                    return;
                }
                try { ValidateSharedFile(shared.Root, shared.Path); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    Revoke(parsed.Token!);
                    await WriteErrorAsync(stream, 404, "Not Found", token).ConfigureAwait(false);
                    return;
                }
                using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(token, shared.Revoked.Token);
                await ServeFileAsync(stream, shared.Path, parsed.Method!, parsed.Range, parsed.IfRange, requestCancellation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { }
            catch (IOException) { }
            catch (SocketException) { }
            catch (UnauthorizedAccessException) { }
            catch (Exception)
            {
                try { await WriteErrorAsync(client.GetStream(), 500, "Internal Server Error", serverToken).ConfigureAwait(false); }
                catch (Exception) { }
            }
            finally { if (entered) _connections.Release(); }
        }
    }

    private static async Task<byte[]?> ReadHeadersAsync(NetworkStream stream, CancellationToken token)
    {
        using var data = new MemoryStream(1024);
        var buffer = new byte[1024];
        while (data.Length < HeaderLimit)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, HeaderLimit - (int)data.Length)), token).ConfigureAwait(false);
            if (read == 0) return null;
            data.Write(buffer, 0, read);
            var bytes = data.GetBuffer();
            var length = (int)data.Length;
            for (var i = Math.Max(0, length - read - 3); i <= length - 4; i++)
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                    return data.ToArray();
        }
        return Array.Empty<byte>();
    }

    private sealed record ParsedRequest(string? Method, string? Token, string? Range, string? IfRange, int ErrorStatus, string ErrorReason);

    private static ParsedRequest ParseRequest(byte[] headerBytes, string? publicHostname, int port)
    {
        if (headerBytes.Length == 0) return new ParsedRequest(null, null, null, null, 431, "Request Header Fields Too Large");
        string text;
        try { text = new UTF8Encoding(false, true).GetString(headerBytes); }
        catch (DecoderFallbackException) { return new ParsedRequest(null, null, null, null, 400, "Bad Request"); }
        var lines = text.Split("\r\n", StringSplitOptions.None);
        var first = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (first.Length != 3 || (first[2] != "HTTP/1.1" && first[2] != "HTTP/1.0"))
            return new ParsedRequest(null, null, null, null, 400, "Bad Request");
        if (first[0] != "GET" && first[0] != "HEAD")
            return new ParsedRequest(null, null, null, null, 405, "Method Not Allowed");
        var target = first[1];
        if (target.Length == 0 || target[0] != '/' || target.Contains('?') || target.Contains('#'))
            return new ParsedRequest(null, null, null, null, 400, "Bad Request");
        var pieces = target.Split('/', StringSplitOptions.None);
        if (pieces.Length != 3 || pieces[0].Length != 0 || pieces[1] != "share" || !IsValidToken(pieces[2]))
            return new ParsedRequest(null, null, null, null, 404, "Not Found");
        string? range = null;
        string? ifRange = null;
        string? host = null;
        foreach (var line in lines.AsSpan(1))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            if (line[..colon].Equals("Range", StringComparison.OrdinalIgnoreCase))
            {
                if (range is not null) return new ParsedRequest(null, null, null, null, 400, "Bad Request");
                range = line[(colon + 1)..].Trim();
            }
            else if (line[..colon].Equals("If-Range", StringComparison.OrdinalIgnoreCase))
            {
                if (ifRange is not null) return new ParsedRequest(null, null, null, null, 400, "Bad Request");
                ifRange = line[(colon + 1)..].Trim();
            }
            else if (line[..colon].Equals("Host", StringComparison.OrdinalIgnoreCase))
            {
                if (host is not null) return new ParsedRequest(null, null, null, null, 400, "Bad Request");
                host = line[(colon + 1)..].Trim();
            }
        }
        if (host is null ||
            (!host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase) &&
             !host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase) &&
             (publicHostname is null ||
              (!host.Equals(publicHostname, StringComparison.OrdinalIgnoreCase) &&
               !host.Equals($"{publicHostname}:443", StringComparison.OrdinalIgnoreCase)))))
            return new ParsedRequest(null, null, null, null, 421, "Misdirected Request");
        return new ParsedRequest(first[0], pieces[2], range, ifRange, 0, "");
    }

    private static bool IsValidToken(string token)
    {
        if (token.Length != 43) return false;
        foreach (var c in token)
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return false;
        return true;
    }

    private static async Task ServeFileAsync(NetworkStream stream, string path, string method, string? rangeHeader,
        string? ifRange, CancellationToken token)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var length = file.Length;
        var modified = File.GetLastWriteTimeUtc(path);
        var modifiedToSecond = new DateTime(modified.Ticks - modified.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc);
        var etag = $"\"{length:x}-{modified.Ticks:x}\"";
        if (ifRange is not null && !string.Equals(ifRange, etag, StringComparison.Ordinal) &&
            (!DateTimeOffset.TryParse(ifRange, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var ifRangeDate) ||
             modifiedToSecond > ifRangeDate.UtcDateTime))
            rangeHeader = null;
        if (rangeHeader?.Contains(',') == true) rangeHeader = null;
        var start = 0L;
        var end = length - 1;
        var partial = false;
        if (rangeHeader is not null && !TryParseRange(rangeHeader, length, out start, out end))
        {
            var response = Encoding.ASCII.GetBytes($"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Range: bytes */{length}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(response, token).ConfigureAwait(false);
            return;
        }
        partial = rangeHeader is not null;
        var count = length == 0 ? 0 : end - start + 1;
        var extension = Path.GetExtension(path);
        var contentType = extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase) ? "video/mp4" :
            extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase) ? "video/x-matroska" : "application/octet-stream";
        var status = partial ? "206 Partial Content" : "200 OK";
        var headers = new StringBuilder()
            .Append("HTTP/1.1 ").Append(status).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append("\r\n")
            .Append("Accept-Ranges: bytes\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\n")
            .Append("ETag: ").Append(etag).Append("\r\n")
            .Append("Last-Modified: ").Append(modified.ToString("R", CultureInfo.InvariantCulture)).Append("\r\nConnection: close\r\n");
        if (partial) headers.Append("Content-Range: bytes ").Append(start).Append('-').Append(end).Append('/').Append(length).Append("\r\n");
        headers.Append("Content-Disposition: inline; filename=ClipDat-clip").Append(extension).Append("\r\n\r\n");
        await WriteWithTimeoutAsync(stream, Encoding.ASCII.GetBytes(headers.ToString()), token).ConfigureAwait(false);
        if (method == "HEAD" || count == 0) return;

        file.Seek(start, SeekOrigin.Begin);
        var buffer = new byte[ChunkBytes];
        var remaining = count;
        while (remaining > 0)
        {
            var wanted = (int)Math.Min(buffer.Length, remaining);
            var read = await file.ReadAsync(buffer.AsMemory(0, wanted), token).ConfigureAwait(false);
            if (read == 0) break;
            await WriteWithTimeoutAsync(stream, buffer.AsMemory(0, read), token).ConfigureAwait(false);
            remaining -= read;
            // Cap each active transfer at about 8 MiB/s to keep sharing lightweight.
            await Task.Delay(TimeSpan.FromMilliseconds(read * 1000.0 / (8 * 1024 * 1024)), token).ConfigureAwait(false);
        }
    }

    private static bool TryParseRange(string header, long length, out long start, out long end)
    {
        start = 0;
        end = Math.Max(0, length - 1);
        if (length <= 0 || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
        var value = header[6..];
        if (value.Contains(',')) return false;
        var dash = value.IndexOf('-');
        if (dash < 0 || value.IndexOf('-', dash + 1) >= 0) return false;
        var left = value[..dash].Trim();
        var right = value[(dash + 1)..].Trim();
        if (left.Length == 0)
        {
            if (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out var suffix) || suffix <= 0) return false;
            start = Math.Max(0, length - suffix);
            end = length - 1;
            return true;
        }
        if (!long.TryParse(left, NumberStyles.None, CultureInfo.InvariantCulture, out start) || start < 0 || start >= length) return false;
        if (right.Length != 0 && (!long.TryParse(right, NumberStyles.None, CultureInfo.InvariantCulture, out end) || end < start)) return false;
        if (right.Length == 0) end = length - 1;
        else end = Math.Min(end, length - 1);
        return true;
    }

    private static async Task WriteErrorAsync(NetworkStream stream, int status, string reason, CancellationToken token)
    {
        var extra = status == 405 ? "Allow: GET, HEAD\r\n" : "";
        var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} {reason}\r\n{extra}Content-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
    }

    private static async Task WriteWithTimeoutAsync(NetworkStream stream, ReadOnlyMemory<byte> bytes, CancellationToken serverToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(serverToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await stream.WriteAsync(bytes, timeout.Token).ConfigureAwait(false);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private void ExpireLinksLocked()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var token in new List<string>(_files.Keys))
            if (_files[token].ExpiresAt <= now)
            {
                _files[token].Revoked.Cancel();
                _files.Remove(token);
            }
    }
}
