using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NexusMods.Tests;

public sealed class HttpTests
{
    [Fact]
    public async Task ApiRequestsAddOnlyTheirHeadersBoundBodiesAndSanitizeNativeFailures()
    {
        using (var client = Fixtures.Client(request =>
        {
            Assert.Equal("nexusmods", Assert.Single(request.Headers.GetValues("Application-Name")));
            Assert.Equal("fake-key", Assert.Single(request.Headers.GetValues("apikey")));
            var response = Fixtures.Json(new { okay = true });
            response.Headers.Add("X-RL-HOURLY-REMAINING", "123");
            return response;
        }))
        {
            var response = await new ApiHttp(client).Send("https://api.nexusmods.com/v1/test", "nexus-v1", "fake-key");
            Assert.Equal("123", response.RateLimits!["hourly_remaining"]);
        }
        using (var client = Fixtures.Client(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[ApiHttp.MaximumBytes + 1]) }))
            await Fixtures.Error("RESPONSE_TOO_LARGE", "nexus-v1", () => new ApiHttp(client).Send("https://api.nexusmods.com/test", "nexus-v1"));
        using (var client = Fixtures.Client(_ => throw new HttpRequestException("sensitive transport details")))
        {
            var error = await Fixtures.Error("API_REQUEST_FAILED", "nexus-v1", () => new ApiHttp(client).Send("https://api.nexusmods.com/test", "nexus-v1"));
            Assert.DoesNotContain("sensitive", error.Message);
        }
        using (var client = Fixtures.Client(_ => new(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream([Encoding.UTF8.GetBytes("{")], new IOException("sensitive body details"))) }))
            await Fixtures.Error("API_RESPONSE_INTERRUPTED", "nexus-v1", () => new ApiHttp(client).Send("https://api.nexusmods.com/test", "nexus-v1"));
    }

    [Fact]
    public async Task CompressedApiResponsesAreDecodedBeforeApplyingTheByteLimit()
    {
        foreach (var oversized in new[] { false, true })
        {
            var json = JsonSerializer.SerializeToUtf8Bytes(new { value = oversized ? new string('x', ApiHttp.MaximumBytes) : "decoded" });
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(json);
            Assert.True(compressed.Length < ApiHttp.MaximumBytes);
            using var listener = Listen();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Reply(listener, "200 OK", "Content-Type: application/json\r\nContent-Encoding: gzip\r\nX-RL-HOURLY-REMAINING: 123\r\n", compressed.ToArray(), stop.Token);
            using var client = ApiHttp.CreateClient();
            try
            {
                var request = new ApiHttp(client).Send(Url(listener, "/api"), "nexus-v2");
                if (oversized) await Fixtures.Error("RESPONSE_TOO_LARGE", "nexus-v2", () => request);
                else
                {
                    var response = await request;
                    Assert.Equal("decoded", response.Value["value"].Text());
                    Assert.Equal("123", response.RateLimits!["hourly_remaining"]);
                }
                Assert.Contains("gzip", await server);
            }
            finally { stop.Cancel(); await server; }
        }
    }

    [Fact]
    public async Task ApiRedirectsAreNotFollowedWithTheApiKey()
    {
        using var listener = Listen();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Reply(listener, "302 Found", "Location: /must-not-follow\r\n", [], stop.Token);
        using var client = ApiHttp.CreateClient();
        try
        {
            await Fixtures.Error("HTTP_302", "nexus-v1", () => new ApiHttp(client).Send(Url(listener, "/api"), "nexus-v1", "fake-key"));
            Assert.Contains("apikey: fake-key", await server);
            Assert.False(listener.Pending());
        }
        finally { stop.Cancel(); await server; }
    }

    [Fact]
    public async Task EncodedCdnResponsesCannotCreateOrRestartTransferFiles()
    {
        foreach (var existing in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            var partial = directory.File("fixture.zip.part");
            var statePath = directory.File("fixture.zip.nexus-state.json");
            var state = JsonSerializer.Serialize(new TransferState(1, Fixtures.Skyrim.Domain, "1", "2", "19", "\"same\"", null), App.Json);
            if (existing) { await File.WriteAllTextAsync(partial, "AAAA"); await File.WriteAllTextAsync(statePath, state); }
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes("nineteen-byte-input"));
            using var listener = Listen();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = Reply(listener, "200 OK", "Content-Encoding: gzip\r\n", compressed.ToArray(), stop.Token);
            using var client = Cdn.CreateClient();
            try
            {
                var request = new DownloadRequest(Fixtures.Skyrim.Domain, "1", "2", "fixture.zip", directory.Path, null, [Url(listener, "/archive")]);
                await Fixtures.Error("UNEXPECTED_DOWNLOAD_CONTENT_ENCODING", "nexus-cdn",
                    () => new Download(new Cdn(client, requireHttps: false)).Run(request));
                Assert.Contains("Accept-Encoding: identity", await server);
                Assert.False(File.Exists(directory.File("fixture.zip")));
                if (existing)
                {
                    Assert.Equal("AAAA", await File.ReadAllTextAsync(partial));
                    Assert.Equal(state, await File.ReadAllTextAsync(statePath));
                    Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
                }
                else Assert.Empty(Directory.GetFiles(directory.Path));
            }
            finally { stop.Cancel(); await server; }
        }
    }

    [Fact]
    public async Task ChunkedResumeCannotExceedItsAnnouncedRangeEvenWhenTheFinalSizeWouldMatch()
    {
        using var directory = new TemporaryDirectory();
        var partial = directory.File("fixture.zip.part");
        var statePath = directory.File("fixture.zip.nexus-state.json");
        var state = JsonSerializer.Serialize(new TransferState(1, Fixtures.Skyrim.Domain, "1", "2", "8", "\"same\"", null), App.Json);
        await File.WriteAllTextAsync(partial, "AAAA");
        await File.WriteAllTextAsync(statePath, state);
        using var listener = Listen();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Reply(listener, "206 Partial Content", "Content-Range: bytes 4-5/8\r\n", Encoding.UTF8.GetBytes("BBBB"), stop.Token, chunked: true);
        using var client = Cdn.CreateClient();
        try
        {
            var request = new DownloadRequest(Fixtures.Skyrim.Domain, "1", "2", "fixture.zip", directory.Path, null, [Url(listener, "/archive")]);
            await Fixtures.Error("SIZE_MISMATCH", "nexus-cdn", () => new Download(new Cdn(client, requireHttps: false)).Run(request));
            Assert.Contains("Range: bytes=4-", await server);
            Assert.False(File.Exists(directory.File("fixture.zip")));
            var retained = await File.ReadAllTextAsync(partial);
            Assert.StartsWith("AAAA", retained);
            Assert.InRange(retained.Length, 4, 6);
            Assert.Equal(state, await File.ReadAllTextAsync(statePath));
        }
        finally { stop.Cancel(); await server; }
    }

    [Fact]
    public async Task CdnRedirectsPreserveResumeHeadersAcrossOriginsWithoutCredentialsOrCookies()
    {
        const string modified = "Tue, 05 May 2026 22:41:21 GMT";
        foreach (var strongEtag in new[] { false, true })
        {
            using var origin = Listen();
            using var destination = Listen();
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            async Task<string[]> Serve()
            {
                var first = await Reply(origin, "302 Found", "Location: /second\r\nSet-Cookie: test=value\r\n", [], stop.Token);
                var second = await Reply(origin, "307 Temporary Redirect", $"Location: {Url(destination, "/archive")}\r\n", [], stop.Token);
                var third = await Reply(destination, "206 Partial Content", "Content-Range: bytes 4-7/8\r\n", Encoding.UTF8.GetBytes("BBBB"), stop.Token);
                return [first, second, third];
            }
            var server = Serve();
            using var client = Cdn.CreateClient();
            try
            {
                var partial = new PartialDownload(4, strongEtag ? "\"same\"" : null, strongEtag ? null : modified);
                var result = await new Cdn(client, requireHttps: false).Open([Url(origin, "/first")], 8, partial);
                using var response = result.Response;
                Assert.Equal(4, result.Offset);
                Assert.Equal("BBBB", await response.Content.ReadAsStringAsync());
                var requests = await server;
                Assert.StartsWith("GET /first ", requests[0]);
                Assert.StartsWith("GET /second ", requests[1]);
                Assert.StartsWith("GET /archive ", requests[2]);
                foreach (var request in requests)
                {
                    Assert.Contains("Range: bytes=4-", request);
                    Assert.Contains(strongEtag ? "If-Match: \"same\"" : $"If-Range: {modified}", request);
                    Assert.Contains("Accept-Encoding: identity", request);
                    foreach (var forbidden in new[] { "apikey:", "authorization:", "application-name:", "application-version:", "cookie:" })
                        Assert.DoesNotContain(forbidden, request.ToLowerInvariant());
                }
            }
            finally { stop.Cancel(); await server; }
        }
    }

    [Fact]
    public async Task CdnRedirectLimitStillAllowsTheNextMirror()
    {
        using var listener = Listen();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        async Task<string[]> Serve()
        {
            var requests = new List<string>();
            for (var i = 0; i <= 5; i++) requests.Add(await Reply(listener, "302 Found", "Location: /loop\r\n", [], stop.Token));
            requests.Add(await Reply(listener, "200 OK", "", Encoding.UTF8.GetBytes("ABCDEFGH"), stop.Token));
            return requests.ToArray();
        }
        var server = Serve();
        using var client = Cdn.CreateClient();
        try
        {
            var result = await new Cdn(client, requireHttps: false).Open([Url(listener, "/loop"), Url(listener, "/archive")], 8);
            using var response = result.Response;
            Assert.Equal("ABCDEFGH", await response.Content.ReadAsStringAsync());
            var requests = await server;
            Assert.Equal(7, requests.Length);
            Assert.All(requests.Take(6), request => Assert.StartsWith("GET /loop ", request));
            Assert.StartsWith("GET /archive ", requests[6]);
        }
        finally { stop.Cancel(); await server; }
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
    private static string Url(TcpListener listener, string path) => $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}{path}";
    private static async Task<string> Reply(TcpListener listener, string status, string headers, byte[] body, CancellationToken cancellation, bool chunked = false)
    {
        using var connection = await listener.AcceptTcpClientAsync(cancellation);
        await using var stream = connection.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        var request = new StringBuilder();
        while (await reader.ReadLineAsync(cancellation) is { Length: > 0 } line) request.AppendLine(line);
        var framing = chunked ? "Transfer-Encoding: chunked" : $"Content-Length: {body.Length}";
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\n{framing}\r\n{headers}\r\n"), cancellation);
        if (chunked && body.Length > 0) await stream.WriteAsync(Encoding.ASCII.GetBytes($"{body.Length:X}\r\n"), cancellation);
        await stream.WriteAsync(body, cancellation);
        if (chunked) await stream.WriteAsync(Encoding.ASCII.GetBytes(body.Length > 0 ? "\r\n0\r\n\r\n" : "0\r\n\r\n"), cancellation);
        return request.ToString();
    }
}
