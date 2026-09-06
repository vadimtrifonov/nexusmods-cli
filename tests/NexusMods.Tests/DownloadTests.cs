using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusMods.Tests;

public sealed class DownloadTests
{
    private const string Date = "Tue, 05 May 2026 22:41:21 GMT";
    private static DownloadRequest Request(TemporaryDirectory directory, string? size = "8") => new(Fixtures.Skyrim.Domain, "1", "2", "fixture.zip", directory.Path, size, ["https://cdn.example.test/archive"]);
    private static string Part(TemporaryDirectory directory) => directory.File("fixture.zip.part");
    private static string State(TemporaryDirectory directory) => directory.File("fixture.zip.nexus-state.json");
    private static void Seed(TemporaryDirectory directory, string? etag = null, string? modified = Date, string bytes = "AAAA")
    {
        File.WriteAllText(Part(directory), bytes);
        File.WriteAllText(State(directory), JsonSerializer.Serialize(new TransferState(1, Fixtures.Skyrim.Domain, "1", "2", "8", etag, modified), App.Json));
    }
    private static HttpResponseMessage Body(string bytes, int status = 200, params (string Name, string Value)[] headers) =>
        Response(new ChunkStream([Encoding.UTF8.GetBytes(bytes)]), status, headers);
    private static HttpResponseMessage Response(Stream stream, int status = 200, params (string Name, string Value)[] headers)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StreamContent(stream) };
        foreach (var (name, value) in headers)
            if (!response.Headers.TryAddWithoutValidation(name, value)) response.Content.Headers.TryAddWithoutValidation(name, value);
        return response;
    }

    [Theory]
    [InlineData("../file.zip")]
    [InlineData("CON.zip")]
    [InlineData("file:stream")]
    [InlineData("file. ")]
    [InlineData("C:\\file.zip")]
    [InlineData("file\u0000.zip")]
    public void UnsafeWindowsBasenamesAreRejected(string name) => Assert.Equal("UNSAFE_ARCHIVE_NAME", Assert.Throws<CliException>(() => Download.ValidateBasename(name)).Error.Code);

    [Fact]
    public async Task KnownTotalsComeFromMetadataContentLengthOrTheCheckpoint()
    {
        foreach (var source in new[] { "metadata", "length", "checkpoint" })
        {
            using var directory = new TemporaryDirectory();
            if (source == "checkpoint") Seed(directory);
            using var client = Fixtures.Client(request =>
            {
                foreach (var header in new[] { "apikey", "authorization", "application-name", "application-version" }) Assert.False(request.Headers.Contains(header));
                if (source == "checkpoint")
                {
                    Assert.Equal("bytes=4-", request.Headers.Range?.ToString());
                    return Body("BBBB", 206, ("Content-Range", "bytes 4-7/8"));
                }
                Assert.Null(request.Headers.Range);
                return source == "length" ? Body("BBBBBBBB", 200, ("Content-Length", "8")) : Body("BBBBBBBB");
            });
            var result = await new Download(new(client)).Run(Request(directory, source == "metadata" ? "8" : null));
            Assert.Equal("8", result.Receipt.SizeBytes);
            Assert.Equal(source == "checkpoint" ? "4" : "0", result.Receipt.ResumedFromByte);
            Assert.Equal(source == "checkpoint" ? "AAAABBBB" : "BBBBBBBB", await File.ReadAllTextAsync(result.Receipt.FinalPath));
            Assert.Empty(result.Errors);
            Assert.Equal([directory.File("fixture.zip")], Directory.GetFiles(directory.Path));
        }
    }

    [Fact]
    public async Task AnInterruptedTransferResumesUsingTheSavedStrongEtag()
    {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        using var client = Fixtures.Client(request =>
        {
            if (calls++ == 0) return Response(new ChunkStream([Encoding.UTF8.GetBytes("AAAA")], new IOException("native error")), 200, ("ETag", "\"version\""));
            Assert.Equal("bytes=4-", request.Headers.Range?.ToString());
            Assert.Equal("\"version\"", Assert.Single(request.Headers.GetValues("If-Match")));
            return Body("BBBB", 206, ("Content-Range", "bytes 4-7/8"), ("ETag", "\"version\""));
        });
        var download = new Download(new(client));
        await Fixtures.Error("DOWNLOAD_INTERRUPTED", "nexus-cdn", () => download.Run(Request(directory)));
        Assert.Equal("AAAA", await File.ReadAllTextAsync(Part(directory)));
        var state = await File.ReadAllTextAsync(State(directory));
        Assert.Equal("8", JsonNode.Parse(state)!["expected_size"]!.GetValue<string>());
        Assert.DoesNotContain("https:", state);
        Assert.DoesNotContain("apikey", state);
        var result = await download.Run(Request(directory));
        Assert.Equal("4", result.Receipt.ResumedFromByte);
        Assert.Equal("AAAABBBB", await File.ReadAllTextAsync(result.Receipt.FinalPath));
    }

    [Fact]
    public async Task SelectedGameScopesApiRequestsCheckpointsAndReceiptsWithoutCrossGameResume()
    {
        foreach (var domain in new[] { "skyrimspecialedition", "fallout4" })
        {
            using var directory = new TemporaryDirectory();
            var calls = 0;
            var transfers = 0;
            using var client = Fixtures.Client(request =>
            {
                calls++;
                if (request.RequestUri!.Host == "api.nexusmods.com")
                {
                    Assert.True(request.Headers.Contains("apikey"));
                    Assert.StartsWith($"/v1/games/{domain}/mods/1/files/2", request.RequestUri.AbsolutePath);
                    if (request.RequestUri.AbsolutePath.EndsWith("download_link.json"))
                        return Fixtures.Json(new[] { new { URI = "https://cdn.example.test/archive" } });
                    return Fixtures.Json(new { file_id = 2, name = "Fixture", file_name = "fixture.zip", version = "1", category_name = "MAIN", size_in_bytes = "8" });
                }
                Assert.False(request.Headers.Contains("apikey"));
                Assert.False(request.Headers.Contains("Application-Name"));
                if (transfers++ == 0) return Response(new ChunkStream([Encoding.UTF8.GetBytes("AAAA")], new IOException("interrupted")), 200, ("ETag", "\"same\""));
                Assert.Equal("bytes=4-", request.Headers.Range!.ToString());
                return Body("BBBB", 206, ("Content-Range", "bytes 4-7/8"), ("ETag", "\"same\""));
            });
            var commands = Fixtures.Commands(client, () => "fake-key");
            string[] args = ["download", "--game", domain, "--mod=1", "--file=2", "--output-dir", directory.Path];
            Assert.Equal("failed", (await Fixtures.Run(commands, args)).Status);
            var saved = await File.ReadAllTextAsync(State(directory));
            Assert.Equal(domain, (string?)JsonNode.Parse(saved)!["game_domain"]);
            var beforeMismatch = calls;
            var other = Request(directory) with { GameDomain = domain == "fallout4" ? "skyrimspecialedition" : "fallout4" };
            await Fixtures.Error("TRANSFER_SOURCE_MISMATCH", "local-file", () => new Download(new Cdn(client)).Run(other));
            Assert.Equal(beforeMismatch, calls);
            Assert.Equal(saved, await File.ReadAllTextAsync(State(directory)));
            Assert.Equal("AAAA", await File.ReadAllTextAsync(Part(directory)));
            var result = await Fixtures.Run(commands, args);
            Assert.Equal("complete", result.Status);
            var data = Fixtures.Node(result.Data!);
            Assert.Equal(domain, (string?)data["source"]!["game_domain"]);
            Assert.Equal(domain, (string?)data["transfer"]!["game_domain"]);
            Assert.Equal("4", (string?)data["transfer"]!["resumed_from_byte"]);
            Assert.Equal("AAAABBBB", await File.ReadAllTextAsync(directory.File("fixture.zip")));
            Assert.Equal([directory.File("fixture.zip")], Directory.GetFiles(directory.Path));
        }
    }

    [Fact]
    public async Task LastModifiedAppendDoesNotReplaceItsCheckpointAndCleanupRetainsTheReceipt()
    {
        using var directory = new TemporaryDirectory();
        Seed(directory);
        var saved = await File.ReadAllTextAsync(State(directory));
        // Read sharing permits inspection but prevents replacement or deletion of this checkpoint.
        using var heldState = new FileStream(State(directory), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var client = Fixtures.Client(request =>
        {
            if (request.RequestUri!.Host == "api.nexusmods.com")
            {
                Assert.True(request.Headers.Contains("apikey"));
                if (request.RequestUri.AbsolutePath.EndsWith("download_link.json")) return Fixtures.Json(new[] { new { URI = "https://cdn.example.test/archive" } });
                return Fixtures.Json(new { file_id = 2, name = "Fixture", file_name = "fixture.zip", version = "1", category_name = "MAIN", size_in_bytes = (string?)null });
            }
            Assert.False(request.Headers.Contains("apikey"));
            Assert.False(request.Headers.Contains("Application-Name"));
            Assert.Equal(Date, Assert.Single(request.Headers.GetValues("If-Range")));
            Assert.False(request.Headers.Contains("If-Match"));
            return Body("BBBB", 206, ("Content-Range", "bytes 4-7/8"), ("Last-Modified", Date), ("ETag", "\"newly-available\""));
        });
        var result = await Fixtures.Commands(client, () => "fake-key").Execute(new DownloadCommand(Fixtures.Skyrim.Domain, "1", "2", directory.Path));
        Assert.Equal("partial", result.Status);
        Assert.Equal("TRANSFER_CLEANUP_FAILED", Assert.Single(result.Errors).Code);
        Assert.Equal("4", (string?)Fixtures.Node(result.Data!)["transfer"]!["resumed_from_byte"]);
        Assert.Equal(saved, await File.ReadAllTextAsync(State(directory)));
        Assert.Equal("AAAABBBB", await File.ReadAllTextAsync(directory.File("fixture.zip")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("W/\"weak\"")]
    public async Task Unsolicited206WithoutAUsableValidatorCannotChangeThePartial(string? etag)
    {
        using var directory = new TemporaryDirectory();
        Seed(directory, etag, null);
        var saved = await File.ReadAllTextAsync(State(directory));
        using var client = Fixtures.Client(request =>
        {
            Assert.Null(request.Headers.Range);
            Assert.False(request.Headers.Contains("If-Match"));
            Assert.False(request.Headers.Contains("If-Range"));
            return Body("BBBB", 206, ("Content-Range", "bytes 4-7/8"));
        });
        await Fixtures.Error("UNEXPECTED_DOWNLOAD_STATUS", "nexus-cdn", () => new Download(new(client)).Run(Request(directory)));
        Assert.Equal("AAAA", await File.ReadAllTextAsync(Part(directory)));
        Assert.Equal(saved, await File.ReadAllTextAsync(State(directory)));
    }

    [Fact]
    public async Task UnknownTotalAndHtmlResponsesAreRejectedBeforeChangingTransferFiles()
    {
        foreach (var type in new[] { "unknown", "text/html; charset=utf-8", "Application/XHTML+XML" })
        foreach (var existing in type == "unknown" ? new[] { false } : [false, true])
        {
            using var directory = new TemporaryDirectory();
            if (existing) Seed(directory);
            var saved = existing ? await File.ReadAllTextAsync(State(directory)) : null;
            var stream = new ChunkStream([Encoding.UTF8.GetBytes("BBBBBBBB")]);
            using var client = Fixtures.Client(_ => Response(stream, 200, type == "unknown" ? [] : [("Content-Type", type)]));
            await Fixtures.Error(type == "unknown" ? "DOWNLOAD_SIZE_UNKNOWN" : "UNEXPECTED_DOWNLOAD_CONTENT_TYPE", "nexus-cdn",
                () => new Download(new(client)).Run(Request(directory, null)));
            Assert.True(stream.Disposed);
            if (existing)
            {
                Assert.Equal(saved, await File.ReadAllTextAsync(State(directory)));
                Assert.Equal("AAAA", await File.ReadAllTextAsync(Part(directory)));
            }
            else Assert.Empty(Directory.GetFiles(directory.Path));
        }
    }

    [Fact]
    public async Task MalformedCheckpointSizesFailLocallyWithoutFetching()
    {
        foreach (var size in new object?[] { null, "bad", "-1", "9223372036854775808", 8 })
        {
            using var directory = new TemporaryDirectory();
            Seed(directory);
            var state = JsonNode.Parse(await File.ReadAllTextAsync(State(directory)))!;
            state["expected_size"] = JsonSerializer.SerializeToNode(size);
            var text = state.ToJsonString();
            await File.WriteAllTextAsync(State(directory), text);
            var called = false;
            using var client = Fixtures.Client(_ => { called = true; return Body("BBBBBBBB"); });
            await Fixtures.Error("TRANSFER_STATE_INVALID", "local-file", () => new Download(new(client)).Run(Request(directory)));
            Assert.False(called);
            Assert.Equal(text, await File.ReadAllTextAsync(State(directory)));
            Assert.Equal("AAAA", await File.ReadAllTextAsync(Part(directory)));
        }
    }

    [Fact]
    public async Task UnsupportedMetadataSizesFailBeforeCdnRequestsOrTransferFileChanges()
    {
        using var directory = new TemporaryDirectory();
        var called = false;
        using var client = Fixtures.Client(_ => { called = true; return Body("BBBBBBBB"); });
        var error = await Fixtures.Error("DOWNLOAD_SIZE_UNSUPPORTED", "local-file",
            () => new Download(new Cdn(client)).Run(Request(directory, "9223372036854775808")));
        Assert.Equal("9223372036854775807", error.Details!["maximum_bytes"]);
        Assert.False(called);
        Assert.Empty(Directory.GetFiles(directory.Path));
    }

    [Fact]
    public async Task RestartTruncatesBeforePublishingReplacementValidators()
    {
        using var directory = new TemporaryDirectory();
        Seed(directory);
        var saved = await File.ReadAllTextAsync(State(directory));
        using var heldState = new FileStream(State(directory), FileMode.Open, FileAccess.Read, FileShare.Read);
        var stream = new ChunkStream([Encoding.UTF8.GetBytes("BBBBBBBB")]);
        using var client = Fixtures.Client(_ => Response(stream, 200, ("ETag", "\"new-version\"")));
        await Fixtures.Error("INTERNAL_ERROR", "local-file", () => new Download(new(client)).Run(Request(directory)));
        Assert.Equal(0, new FileInfo(Part(directory)).Length);
        Assert.Equal(saved, await File.ReadAllTextAsync(State(directory)));
        Assert.True(stream.Disposed);
        Assert.Equal(2, Directory.GetFiles(directory.Path).Length);
    }

    [Fact]
    public async Task RestartWithoutAValidatorReplacesOldBytesWithAFullResponse()
    {
        using var directory = new TemporaryDirectory();
        Seed(directory, modified: null);
        using var client = Fixtures.Client(request => { Assert.Null(request.Headers.Range); return Body("BBBBBBBB"); });
        var result = await new Download(new(client)).Run(Request(directory));
        Assert.Equal("0", result.Receipt.ResumedFromByte);
        Assert.Equal("BBBBBBBB", await File.ReadAllTextAsync(result.Receipt.FinalPath));
    }

    [Fact]
    public async Task ExcessChunksNeverWriteBeyondTheBudgetAndFullSizedPartialsRestart()
    {
        foreach (var resume in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            if (resume) Seed(directory);
            var retry = false;
            using var client = Fixtures.Client(request =>
            {
                if (retry) { Assert.Null(request.Headers.Range); return Body("BBBBBBBB"); }
                var stream = new ChunkStream([Encoding.UTF8.GetBytes(resume ? "AAAA" : "AAAAAAAA"), Encoding.UTF8.GetBytes("X")]);
                return Response(stream, resume ? 206 : 200, resume ? [("Content-Range", "bytes 4-7/8")] : []);
            });
            var download = new Download(new(client));
            await Fixtures.Error("SIZE_MISMATCH", "nexus-cdn", () => download.Run(Request(directory)));
            Assert.Equal("AAAAAAAA", await File.ReadAllTextAsync(Part(directory)));
            retry = true;
            var result = await download.Run(Request(directory));
            Assert.Equal("0", result.Receipt.ResumedFromByte);
            Assert.Equal("BBBBBBBB", await File.ReadAllTextAsync(result.Receipt.FinalPath));
        }
    }

    [Fact]
    public async Task ResponseBodyLengthAndFinalFileLengthAreIndependentChecks()
    {
        foreach (var kind in new[] { "overlong", "short-body", "short-range" })
        {
            using var directory = new TemporaryDirectory();
            Seed(directory);
            var saved = await File.ReadAllTextAsync(State(directory));
            var retry = false;
            using var client = Fixtures.Client(request =>
            {
                if (retry)
                {
                    Assert.Equal("bytes=6-", request.Headers.Range!.ToString());
                    return Body("CC", 206, ("Content-Range", "bytes 6-7/8"));
                }
                var bytes = Encoding.UTF8.GetBytes("BB");
                return Response(new ChunkStream(kind == "overlong" ? [bytes, bytes] : [bytes]), 206,
                    ("Content-Range", kind == "short-body" ? "bytes 4-7/8" : "bytes 4-5/8"));
            });
            var download = new Download(new Cdn(client));
            var error = await Fixtures.Error("SIZE_MISMATCH", "nexus-cdn", () => download.Run(Request(directory)));
            if (kind == "short-body")
            {
                Assert.Equal("4", error.Details!["expected_body_size"]);
                Assert.Equal("2", error.Details!["received_body_size"]);
            }
            if (kind == "short-range")
            {
                Assert.Equal("8", error.Details!["expected_size"]);
                Assert.Equal("6", error.Details!["received_size"]);
            }
            Assert.Equal("AAAABB", await File.ReadAllTextAsync(Part(directory)));
            Assert.Equal(saved, await File.ReadAllTextAsync(State(directory)));
            Assert.False(File.Exists(directory.File("fixture.zip")));
            retry = true;
            var result = await download.Run(Request(directory));
            Assert.Equal("6", result.Receipt.ResumedFromByte);
            Assert.Equal("AAAABBCC", await File.ReadAllTextAsync(result.Receipt.FinalPath));
        }
    }

    [Fact]
    public async Task WrappedFailuresKeepTheirCausesAndThrowSitesOutOfPublicJson()
    {
        using var directory = new TemporaryDirectory();
        var cause = new IOException("fake-secret native detail");
        using var client = Fixtures.Client(_ => Response(new ChunkStream([], cause)));
        var exception = await Assert.ThrowsAsync<CliException>(() => new Download(new Cdn(client)).Run(Request(directory)));
        Assert.Same(cause, exception.GetBaseException());
        Exception original = exception;
        while (original.InnerException != cause) original = Assert.IsAssignableFrom<Exception>(original.InnerException);
        var wrappingMethod = new StackTrace(cause).GetFrames()
            .Last(frame => frame.GetMethod()?.DeclaringType?.Assembly == typeof(Download).Assembly).GetMethod();
        Assert.Contains(new StackTrace(original).GetFrames(), frame => frame.GetMethod() == wrappingMethod);
        Assert.Contains(nameof(ChunkStream.ReadAsync), cause.StackTrace);
        var error = CliException.Public(exception);
        Assert.Equal("DOWNLOAD_INTERRUPTED", error.Code);
        Assert.Equal("nexus-cdn", error.Source);
        var json = JsonSerializer.Serialize(error, App.Json);
        Assert.DoesNotContain("fake-secret", json);
        Assert.DoesNotContain("StackTrace", json);
        Assert.DoesNotContain("inner_exception", json);
    }

    [Fact]
    public async Task OnlyOneInvocationOwnsTheTransferUntilFinalization()
    {
        using var directory = new TemporaryDirectory();
        Seed(directory);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var client = new HttpClient(new StubHttp(_ => { calls++; ready.SetResult(); return response.Task; }));
        var download = new Download(new(client));
        var first = download.Run(Request(directory));
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Fixtures.Error("DOWNLOAD_LOCKED", "local-file", () => download.Run(Request(directory)));
            Assert.Equal(1, calls);
        }
        finally { response.TrySetResult(Body("BBBB", 206, ("Content-Range", "bytes 4-7/8"))); await first; }
        Assert.Equal("AAAABBBB", await File.ReadAllTextAsync(directory.File("fixture.zip")));
    }

    [Fact]
    public async Task InitialCreationRollbackRemovesOnlyOwnedArtifacts()
    {
        using var directory = new TemporaryDirectory();
        var collision = true;
        using var client = Fixtures.Client(_ =>
        {
            if (collision) File.WriteAllText(State(directory), "keep");
            return Body("BBBBBBBB");
        });
        var download = new Download(new(client));
        await Fixtures.Error("TRANSFER_STATE_CREATE_FAILED", "local-file", () => download.Run(Request(directory)));
        Assert.Equal("keep", await File.ReadAllTextAsync(State(directory)));
        Assert.False(File.Exists(Part(directory)));
        collision = false;
        File.Delete(State(directory));
        Assert.Empty((await download.Run(Request(directory))).Errors);
    }

    [Fact]
    public async Task FinalPublicationDoesNotOverwriteAnExistingDestination()
    {
        foreach (var before in new[] { false, true })
        {
            using var directory = new TemporaryDirectory();
            if (before) File.WriteAllText(directory.File("fixture.zip"), "keep");
            using var client = Fixtures.Client(_ =>
            {
                Assert.False(before);
                File.WriteAllText(directory.File("fixture.zip"), "keep");
                return Body("BBBBBBBB");
            });
            await Fixtures.Error("DESTINATION_EXISTS", "local-file", () => new Download(new(client)).Run(Request(directory)));
            Assert.Equal("keep", await File.ReadAllTextAsync(directory.File("fixture.zip")));
        }
    }

    [Fact]
    public async Task LockCleanupFailureDoesNotReplaceTheTransferFailure()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("fixture.zip.nexus-lock");
        using var client = Fixtures.Client(_ =>
        {
            File.SetAttributes(path, FileAttributes.ReadOnly);
            return Response(new ChunkStream([], new IOException("transport failure")));
        });
        try
        {
            var error = await Fixtures.Error("DOWNLOAD_INTERRUPTED", "nexus-cdn", () => new Download(new(client)).Run(Request(directory)));
            Assert.True(error.Details!.ContainsKey("cleanup_errors"));
            Assert.True(File.Exists(path));
        }
        finally { if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal); }
    }

    [Fact]
    public async Task RealHttpBodyInactivityIsBoundedAfterHeaders()
    {
        using var directory = new TemporaryDirectory();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var server = Task.Run(async () =>
        {
            using var connection = await listener.AcceptTcpClientAsync(stop.Token);
            await using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
            while (await reader.ReadLineAsync(stop.Token) is { Length: > 0 }) { }
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 8\r\nETag: \"version\"\r\n\r\nAAAA"), stop.Token);
            try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        });
        using var client = Cdn.CreateClient();
        try
        {
            var request = Request(directory) with { Mirrors = [$"http://127.0.0.1:{port}/archive"] };
            await Fixtures.Error("DOWNLOAD_INTERRUPTED", "nexus-cdn", () => new Download(new(client, requireHttps: false), TimeSpan.FromMilliseconds(100)).Run(request));
            Assert.Equal("AAAA", await File.ReadAllTextAsync(Part(directory)));
            Assert.False(File.Exists(directory.File("fixture.zip")));
        }
        finally { stop.Cancel(); await server; }
    }
}
