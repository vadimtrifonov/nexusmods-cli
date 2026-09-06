using System.Net;
using System.Text;
using Xunit;

namespace NexusMods.Tests;

public sealed class CdnTests
{
    [Fact]
    public async Task InsecureUrlsAndUnusableMirrorsAreSkippedWithoutSendingCredentials()
    {
        var calls = new List<string>();
        using var client = Fixtures.Client(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            calls.Add(url);
            foreach (var header in new[] { "apikey", "authorization", "application-name", "application-version" }) Assert.False(request.Headers.Contains(header));
            if (url.EndsWith("unavailable")) return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes("ABCDEFGH")) };
            response.Content.Headers.ContentLength = 8;
            return response;
        });
        var result = await new Cdn(client).Open(["http://cdn.example.test/unsafe", "https://cdn.example.test/unavailable", "https://cdn.example.test/archive"], null);
        using var body = result.Response;
        Assert.Equal(new[] { "https://cdn.example.test/unavailable", "https://cdn.example.test/archive" }, calls);
        Assert.Equal(8, result.ExpectedSize);
        Assert.Equal("ABCDEFGH", await body.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ContentCodingsMustBeIdentityIncludingOnPartialResponses()
    {
        foreach (var encoding in new[] { "identity", "IDENTITY, identity", "gzip", "br", "identity, gzip", "gzip/" })
        {
            var stream = new ChunkStream([Encoding.UTF8.GetBytes("BBBB")]);
            using var client = Fixtures.Client(_ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new StreamContent(stream) };
                response.Content.Headers.TryAddWithoutValidation("Content-Encoding", encoding);
                response.Content.Headers.ContentRange = new(4, 7, 8);
                return response;
            });
            var request = new Cdn(client).Open(["https://cdn.example.test/archive"], 8, new(4, "\"same\"", null));
            if (encoding is "identity" or "IDENTITY, identity")
            {
                var result = await request;
                using var response = result.Response;
                Assert.Equal(4, result.ExpectedBodySize);
                Assert.Equal("BBBB", await response.Content.ReadAsStringAsync());
            }
            else await Fixtures.Error("UNEXPECTED_DOWNLOAD_CONTENT_ENCODING", "nexus-cdn", () => request);
            Assert.True(stream.Disposed);
        }
    }

    [Fact]
    public async Task BufferedContentDoesNotSubstituteForAMissingTotalHeader()
    {
        using var client = Fixtures.Client(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([1, 2, 3, 4]) });
        await Fixtures.Error("DOWNLOAD_SIZE_UNKNOWN", "nexus-cdn", () => new Cdn(client).Open(["https://cdn.example.test/archive"], null));
    }

    [Fact]
    public async Task RangeNegotiationSupportsTheInt64BoundaryWithoutNarrowingOffsets()
    {
        var offset = long.MaxValue - 4;
        using var client = Fixtures.Client(request =>
        {
            Assert.Equal($"bytes={offset}-", request.Headers.Range!.ToString());
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.ContentLength = 4;
            response.Content.Headers.ContentRange = new(offset, long.MaxValue - 1, long.MaxValue);
            return response;
        });
        var result = await new Cdn(client).Open(["https://cdn.example.test/archive"], long.MaxValue, new(offset, "\"same\"", null));
        using var response = result.Response;
        Assert.Equal(offset, result.Offset);
        Assert.Equal(long.MaxValue, result.ExpectedSize);
    }

    [Fact]
    public async Task ExhaustionReportsOnlyAttemptCount()
    {
        var calls = 0;
        var cause = new IOException("private transport details");
        using var client = Fixtures.Client(_ => { calls++; throw cause; });
        var exception = await Assert.ThrowsAsync<CliException>(() => new Cdn(client).Open(["https://cdn.example.test/one", "https://cdn.example.test/two"], 8));
        Assert.Same(cause, exception.GetBaseException());
        var error = CliException.Public(exception);
        Assert.Equal("ALL_MIRRORS_FAILED", error.Code);
        Assert.Equal("nexus-cdn", error.Source);
        Assert.Equal(2, calls);
        Assert.Equal(2, error.Details!["attempts"]);
        Assert.DoesNotContain("private", error.Message);
    }

    [Theory]
    [InlineData("bytes 3-7/8", "5", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes 4-8/9", "5", "\"same\"", "SIZE_MISMATCH")]
    [InlineData("bytes 4-7/8", "3", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes 4-7/8", "4", "\"different\"", "RESOURCE_CHANGED")]
    [InlineData("items 4-7/8", "4", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes 4-7/*", "4", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes */8", "4", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes 7-4/8", "4", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes 4-8/8", "4", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes 4-7/9223372036854775808", "4", "\"same\"", "INVALID_CONTENT_RANGE")]
    [InlineData("bytes 4-7/8", "bad", "\"same\"", "INVALID_DOWNLOAD_RESPONSE")]
    [InlineData("bytes 4-7/8", "9223372036854775808", "\"same\"", "INVALID_DOWNLOAD_RESPONSE")]
    public async Task ResumeRequiresMatchingRangeTotalLengthAndValidator(string range, string length, string etag, string code)
    {
        using var client = Fixtures.Client(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([1, 2, 3, 4]) };
            response.Content.Headers.TryAddWithoutValidation("Content-Range", range);
            response.Content.Headers.TryAddWithoutValidation("Content-Length", length);
            response.Headers.TryAddWithoutValidation("ETag", etag);
            return response;
        });
        await Fixtures.Error(code, "nexus-cdn", () => new Cdn(client).Open(["https://cdn.example.test/archive"], 8, new(4, "\"same\"", null)));
    }
}
