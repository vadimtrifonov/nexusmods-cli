using System.Globalization;
using System.Net;
using System.Net.Http.Headers;

namespace NexusMods;

internal sealed record PartialDownload(long Size, string? Etag, string? LastModified);
internal sealed record CdnResponse(HttpResponseMessage Response, long Offset, long ExpectedSize, long ExpectedBodySize, string? Etag, string? LastModified);

// Protocol negotiation only: no credentials, Nexus record identities, or filesystem state.
internal sealed class Cdn(HttpClient client, bool requireHttps = true)
{
    private const string Source = "nexus-cdn";
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);

    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.None,
        UseCookies = false
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static bool StrongEtag(string? value) => EntityTagHeaderValue.TryParse(value, out var tag) && !tag.IsWeak && tag.Tag != "*";
    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) || response.Content.Headers.TryGetValues(name, out values)
            ? string.Join(", ", values) : null;
    private static bool Date(string? text, out DateTimeOffset time) => DateTimeOffset.TryParse(text,
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out time);
    private static long? Length(string? text)
    {
        // ContentLength can infer a replacement for a missing or malformed header from buffered content.
        if (text is null) return null;
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var length)) return length;
        throw new CliException("INVALID_DOWNLOAD_RESPONSE", "The CDN returned an invalid or unsupported Content-Length header.");
    }
    private static (long Start, long End, long Total) Range(string? text)
    {
        if (!ContentRangeHeaderValue.TryParse(text, out var range)
            || !range.Unit.Equals("bytes", StringComparison.OrdinalIgnoreCase)
            || range is not { From: { } start, To: { } end, Length: { } total })
            throw new CliException("INVALID_CONTENT_RANGE", "The CDN returned an invalid or unsupported Content-Range header.");
        return (start, end, total);
    }

    private async Task<HttpResponseMessage> Fetch(string url, PartialDownload? resume)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new CliException("INVALID_DOWNLOAD_URL", "Nexus returned an invalid download URL.");
        if (requireHttps && uri.Scheme != "https") throw new CliException("INSECURE_DOWNLOAD_URL", "The CDN URL was not HTTPS.");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add("Accept", "application/octet-stream, application/zip, application/x-7z-compressed, */*");
        request.Headers.Add("Accept-Encoding", "identity");
        request.Headers.Add("User-Agent", App.UserAgent);
        if (resume is not null)
        {
            request.Headers.Range = new RangeHeaderValue(resume.Size, null);
            if (StrongEtag(resume.Etag)) request.Headers.Add("If-Match", resume.Etag!);
            else request.Headers.Add("If-Range", resume.LastModified!);
        }
        // The handler follows at most five redirects and rejects HTTPS-to-HTTP downgrades.
        using var timeout = new CancellationTokenSource(HeaderTimeout);
        try { return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (Exception exception) { throw new CliException("CDN_REQUEST_FAILED", "The CDN request could not be completed.", innerException: exception); }
    }

    internal Task<CdnResponse> Open(IReadOnlyList<string> mirrors, long? expected, PartialDownload? partial = null) =>
        CliException.From(Source, async () =>
        {
            // Existing bytes alone do not authorize append. A full-sized partial also needs a new successful EOF.
            var resume = partial is { Size: > 0 } && expected is { } total && partial.Size < total
                && (StrongEtag(partial.Etag) || partial.LastModified is not null) ? partial : null;
            Exception? lastFailure = null;
            for (var index = 0; index < mirrors.Count; index++)
            {
                HttpResponseMessage response;
                try { response = await Fetch(mirrors[index], resume); }
                catch (Exception exception) { lastFailure = exception; continue; }
                try
                {
                    // Inspect supplied values: the typed collection can omit malformed content codings.
                    var encoding = Header(response, "Content-Encoding");
                    if (encoding is not null && encoding.Split(',').Any(value => !value.Trim(' ', '\t').Equals("identity", StringComparison.OrdinalIgnoreCase)))
                        throw new CliException("UNEXPECTED_DOWNLOAD_CONTENT_ENCODING", "The CDN returned a non-identity content encoding.");
                    var mime = Header(response, "Content-Type")?.Split(';')[0].Trim().ToLowerInvariant();
                    if (mime is "text/html" or "application/xhtml+xml")
                        throw new CliException("UNEXPECTED_DOWNLOAD_CONTENT_TYPE", "The CDN returned an HTML content type.");
                    var etag = Header(response, "ETag");
                    var modified = Header(response, "Last-Modified");
                    if (!Date(modified, out _)) modified = null;
                    var length = Length(Header(response, "Content-Length"));
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        if (expected is not null && length is not null && expected != length)
                            throw new CliException("SIZE_MISMATCH", "The CDN response size differs from the expected size.", details:
                                new() { ["expected_size"] = expected.ToString(), ["response_size"] = length.ToString() });
                        var size = expected ?? length ?? throw new CliException("DOWNLOAD_SIZE_UNKNOWN", "The download has no known total byte size.");
                        return new CdnResponse(response, 0, size, size, etag, modified);
                    }
                    if (resume is null) throw new CliException("UNEXPECTED_DOWNLOAD_STATUS", "The CDN did not return a complete response.");
                    if (response.StatusCode == HttpStatusCode.PartialContent)
                    {
                        var range = Range(Header(response, "Content-Range"));
                        if (range.Start != resume.Size) throw new CliException("INVALID_CONTENT_RANGE", "The CDN resumed from the wrong byte offset.");
                        if (range.Total != expected)
                            throw new CliException("SIZE_MISMATCH", "The resumed CDN response has a different total size.", details:
                                new() { ["expected_size"] = expected.ToString(), ["response_size"] = range.Total.ToString(CultureInfo.InvariantCulture) });
                        var bodySize = range.End - range.Start + 1;
                        if (length is not null && length != bodySize)
                            throw new CliException("INVALID_CONTENT_RANGE", "Content-Length does not match Content-Range.");
                        if (resume.Etag is not null && etag is not null && resume.Etag != etag
                            || Date(resume.LastModified, out var oldDate) && Date(modified, out var newDate) && oldDate != newDate)
                            throw new CliException("RESOURCE_CHANGED", "The CDN resource validator changed during resume.");
                        return new CdnResponse(response, resume.Size, range.Total, bodySize, resume.Etag, resume.LastModified);
                    }
                    throw new CliException("UNEXPECTED_DOWNLOAD_STATUS", "The CDN returned an unexpected status.");
                }
                catch (Exception exception)
                {
                    response.Dispose();
                    lastFailure = exception;
                    if (index == mirrors.Count - 1 && exception is CliException) throw;
                }
            }
            throw new CliException("ALL_MIRRORS_FAILED", "No Nexus download mirror returned a usable response.",
                details: new() { ["attempts"] = mirrors.Count }, innerException: lastFailure);
        });
}
