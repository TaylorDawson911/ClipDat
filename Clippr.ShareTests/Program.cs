using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Clippr;

if (args.Length == 2 && args[0] == "--live")
{
    var provider = new AnonMp4ShareService();
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
    var uploaded = await provider.UploadAsync(args[1], null, timeout.Token);
    Console.WriteLine($"Live upload accepted: {uploaded.VideoId}, {uploaded.WatchUrl}");
    Console.WriteLine("Processing status: " + await provider.GetStatusAsync(uploaded.VideoId, timeout.Token));
    return;
}

var folder = Path.Combine(Path.GetTempPath(), "ClipDat-share-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
try
{
    var video = Path.Combine(folder, "sample.mp4");
    await File.WriteAllBytesAsync(video, [0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109, 0, 0, 0, 0]);
    var handler = new StubHandler(async request =>
    {
        Require(request.Method == HttpMethod.Post && request.RequestUri?.AbsoluteUri == "https://anonmp4api.xyz/upload", "upload endpoint");
        Require(request.Content?.Headers.ContentType?.MediaType == "multipart/form-data", "multipart content");
        var body = await request.Content!.ReadAsByteArrayAsync();
        Require(Encoding.UTF8.GetString(body).Contains("name=file", StringComparison.Ordinal) ||
                Encoding.UTF8.GetString(body).Contains("name=\"file\"", StringComparison.Ordinal), "file field");
        return Json("""{"success":true,"video_id":"abc123","watch_url":"https://anonmp4.to/v/abc123","message":"File received and queued for processing"}""");
    });
    using var client = new HttpClient(handler);
    var service = new AnonMp4ShareService(client);
    var result = await service.UploadAsync(video, null, CancellationToken.None);
    Require(result.VideoId == "abc123" && result.Status == "processing", "validated upload result");
    Require(result.WatchUrl == "https://anonmp4.to/v/abc123", "server URL used");
    var mkv = Path.Combine(folder, "sample.mkv");
    await File.WriteAllBytesAsync(mkv, [0x1A, 0x45, 0xDF, 0xA3, 0, 0, 0, 0, 0, 0, 0, 0]);
    Require((await service.UploadAsync(mkv, null, CancellationToken.None)).VideoId == "abc123", "MKV upload");
    var larger = Path.Combine(folder, "larger.mp4");
    var largerBytes = new byte[2 * 1024 * 1024];
    new byte[] { 0, 0, 0, 24, 102, 116, 121, 112, 105, 115, 111, 109 }.CopyTo(largerBytes, 0);
    await File.WriteAllBytesAsync(larger, largerBytes);
    long lastSent = 0;
    var progress = new DirectProgress(value => lastSent = value.Sent);
    await service.UploadAsync(larger, progress, CancellationToken.None);
    Require(lastSent == largerBytes.Length, "real streamed progress");
    handler.Handle = _ => Task.FromResult(Json("""{"success":true,"video_id":"abc123","watch_url":"https://anonmp4.art/v/abc123"}"""));
    Require((await service.UploadAsync(video, null, CancellationToken.None)).WatchUrl ==
        "https://anonmp4.art/v/abc123", "observed AnonMP4 watch domain");
    var history = new ShareHistory(Path.Combine(folder, "share-history.json"));
    history.Save(result);
    Require(history.FindCurrent(video)?.VideoId == "abc123", "history reuse");
    history.Save(result with { WatchUrl = "https://evil.example/v/abc123" });
    Require(history.FindCurrent(video)?.WatchUrl == result.WatchUrl, "reject tampered history URL");
    await File.AppendAllTextAsync(video, "changed");
    Require(history.FindCurrent(video) is null, "changed file must not reuse URL");
    handler.Handle = _ => Task.FromResult(Json("""{"success":true,"video_id":"abc123","watch_url":"https://evil.example/v/abc123"}"""));
    await Expect<InvalidDataException>(() => service.UploadAsync(video, null, CancellationToken.None), "reject foreign URL");
    handler.Handle = _ => Task.FromResult(Json("""{"success":true,"watch_url":"https://anonmp4.to/v/abc123"}"""));
    await Expect<InvalidDataException>(() => service.UploadAsync(video, null, CancellationToken.None), "reject missing ID");
    handler.Handle = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
    await Expect<HttpRequestException>(() => service.UploadAsync(video, null, CancellationToken.None), "rate limit error");
    handler.Handle = _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    await Expect<HttpRequestException>(() => service.UploadAsync(video, null, CancellationToken.None), "server error");
    handler.Handle = _ => Task.FromResult(Json("not-json"));
    await Expect<System.Text.Json.JsonException>(() => service.UploadAsync(video, null, CancellationToken.None), "invalid JSON");
    var image = Path.Combine(folder, "image.png");
    await File.WriteAllBytesAsync(image, [1]);
    await Expect<InvalidOperationException>(() => service.UploadAsync(image, null, CancellationToken.None), "reject screenshots");
    var invalidVideo = Path.Combine(folder, "invalid.mp4");
    await File.WriteAllBytesAsync(invalidVideo, new byte[32]);
    await Expect<InvalidDataException>(() => service.UploadAsync(invalidVideo, null, CancellationToken.None), "reject invalid video");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Expect<OperationCanceledException>(() => service.UploadAsync(video, null, cancelled.Token), "cancellation");
    handler.Handle = _ => Task.FromResult(Json("""{"success":true,"status":"active"}"""));
    Require(await service.GetStatusAsync("abc123", CancellationToken.None) == "ready", "info status");
    Console.WriteLine("AnonMP4 mocked upload checks passed.");
}
finally { Directory.Delete(folder, true); }

static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    { Content = new StringContent(value, Encoding.UTF8, "application/json") };
static void Require(bool valid, string name) { if (!valid) throw new Exception("Failed: " + name); }
static async Task Expect<T>(Func<Task> action, string name) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception("Failed: " + name);
}
sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler) : HttpMessageHandler
{
    public Func<HttpRequestMessage, Task<HttpResponseMessage>> Handle { get; set; } = handler;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Handle(request);
}
sealed class DirectProgress(Action<(long Sent, long Total)> report) : IProgress<(long Sent, long Total)>
{
    public void Report((long Sent, long Total) value) => report(value);
}
