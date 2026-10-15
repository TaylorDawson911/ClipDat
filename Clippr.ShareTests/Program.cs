using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using Clippr;

var root = Path.Combine(AppContext.BaseDirectory, "test-clips");
Directory.CreateDirectory(root);
var clip = Path.Combine(root, "share-test.mp4");
await File.WriteAllBytesAsync(clip, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);

await using var server = new LocalClipShareServer();
var link = server.CreateLink(root, clip, TimeSpan.FromMinutes(1));
using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };

using (var full = await client.GetAsync(link.Url))
{
    Require(full.StatusCode == HttpStatusCode.OK, "full response status");
    Require((await full.Content.ReadAsByteArrayAsync()).Length == 10, "full response length");
}

using (var request = new HttpRequestMessage(HttpMethod.Get, link.Url))
{
    request.Headers.Range = new RangeHeaderValue(3, 5);
    using var partial = await client.SendAsync(request);
    Require(partial.StatusCode == HttpStatusCode.PartialContent, "range response status");
    Require((await partial.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(new byte[] { 3, 4, 5 }), "range response bytes");
}

using (var request = new HttpRequestMessage(HttpMethod.Head, link.Url))
{
    using var head = await client.SendAsync(request);
    Require(head.StatusCode == HttpStatusCode.OK && head.Content.Headers.ContentLength == 10, "HEAD response");
}

using (var request = new HttpRequestMessage(HttpMethod.Get, link.Url))
{
    request.Headers.Range = new RangeHeaderValue(null, 3);
    using var suffix = await client.SendAsync(request);
    Require(suffix.StatusCode == HttpStatusCode.PartialContent, "suffix range status");
    Require((await suffix.Content.ReadAsByteArrayAsync()).AsSpan().SequenceEqual(new byte[] { 7, 8, 9 }), "suffix range bytes");
}

using (var request = new HttpRequestMessage(HttpMethod.Get, link.Url))
{
    request.Headers.Range = new RangeHeaderValue(2, 4);
    request.Headers.TryAddWithoutValidation("If-Range", "\"stale-etag\"");
    using var stale = await client.SendAsync(request);
    Require(stale.StatusCode == HttpStatusCode.OK && (await stale.Content.ReadAsByteArrayAsync()).Length == 10,
        "stale If-Range returns full response");
}

using (var probe = await client.GetAsync(link.Url))
using (var request = new HttpRequestMessage(HttpMethod.Get, link.Url))
{
    request.Headers.Range = new RangeHeaderValue(2, 4);
    request.Headers.TryAddWithoutValidation("If-Range", probe.Headers.ETag!.ToString());
    using var matching = await client.SendAsync(request);
    Require(matching.StatusCode == HttpStatusCode.PartialContent, "matching If-Range ETag returns partial response");
}

using (var request = new HttpRequestMessage(HttpMethod.Get, link.Url))
{
    request.Headers.Host = "unexpected.example";
    using var wrongHost = await client.SendAsync(request);
    Require(wrongHost.StatusCode == HttpStatusCode.MisdirectedRequest, "unapproved Host rejection");
}

using (var request = new HttpRequestMessage(HttpMethod.Get, link.Url))
{
    request.Headers.Range = new RangeHeaderValue(50, 60);
    using var invalidRange = await client.SendAsync(request);
    Require(invalidRange.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable, "unsatisfiable range status");
}

Require(server.Revoke(link.Token), "token revocation");
using (var revoked = await client.GetAsync(link.Url)) Require(revoked.StatusCode == HttpStatusCode.NotFound, "revoked token rejection");

var outside = Path.Combine(AppContext.BaseDirectory, "outside.mp4");
await File.WriteAllBytesAsync(outside, [1]);
RequireThrows<UnauthorizedAccessException>(() => server.CreateLink(root, outside, TimeSpan.FromMinutes(1)), "outside-folder rejection");
RequireThrows<ArgumentException>(() => server.CreateLink(root, Path.Combine(root, "image.png"), TimeSpan.FromMinutes(1)), "non-video rejection");

var expiring = server.CreateLink(root, clip, TimeSpan.FromMilliseconds(1));
await Task.Delay(50);
using (var expired = await client.GetAsync(expiring.Url)) Require(expired.StatusCode == HttpStatusCode.NotFound, "expired token rejection");

var first = server.CreateLink(root, clip, TimeSpan.FromMinutes(1));
var second = server.CreateLink(root, clip, TimeSpan.FromMinutes(1));
var third = server.CreateLink(root, clip, TimeSpan.FromMinutes(1));
RequireThrows<InvalidOperationException>(() => server.CreateLink(root, clip, TimeSpan.FromMinutes(1)), "three-share limit");
Require(server.Revoke(first.Token), "independent first share revocation");
using (var stillShared = await client.GetAsync(second.Url)) Require(stillShared.StatusCode == HttpStatusCode.OK, "other share remains active");
Require(server.Revoke(second.Token) && server.Revoke(third.Token), "remaining shares revoke");

Console.WriteLine("Local share server smoke checks passed.");

static void Require(bool condition, string description)
{
    if (!condition) throw new Exception($"Failed: {description}");
}

static void RequireThrows<T>(Action action, string description) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Failed: {description}");
}
