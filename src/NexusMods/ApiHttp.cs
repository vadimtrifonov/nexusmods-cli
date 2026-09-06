using System.Net;
using System.Text;
using System.Text.Json;

namespace NexusMods;

internal sealed record ApiResponse(JsonValue Value, Dictionary<string, string>? RateLimits);

internal sealed class ApiHttp(HttpClient client)
{
    internal const int MaximumBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    // Redirects must not forward the per-request API key to another origin.
    internal static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        UseCookies = false
    }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    internal async Task<ApiResponse> Send(string url, string source, string? key = null, object? body = null,
        CancellationToken cancellation = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(Timeout);
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("Application-Name", App.Name);
        request.Headers.Add("Application-Version", App.Version);
        request.Headers.Add("User-Agent", App.UserAgent);
        if (key is not null) request.Headers.Add("apikey", key);
        if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token); }
        catch (Exception exception) { throw new CliException("API_REQUEST_FAILED", "The API request could not be completed.", source, innerException: exception); }
        using (response)
        {
            var limits = ReadLimits(response);
            using var bytes = new MemoryStream();
            try
            {
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                var buffer = new byte[32 * 1024];
                while (true)
                {
                    var count = await input.ReadAsync(buffer, timeout.Token);
                    if (count == 0) break;
                    if (bytes.Length + count > MaximumBytes)
                        throw new CliException("RESPONSE_TOO_LARGE", "The API response exceeded the configured limit.", source,
                            new() { ["maximum_bytes"] = MaximumBytes.ToString() });
                    bytes.Write(buffer, 0, count);
                }
            }
            catch (CliException) { throw; }
            catch (Exception exception) { throw new CliException("API_RESPONSE_INTERRUPTED", "The API response could not be read completely.", source, innerException: exception); }

            if (!response.IsSuccessStatusCode)
                throw new CliException(new PublicError($"HTTP_{(int)response.StatusCode}", "The API returned an unsuccessful response.",
                    source, HttpStatus: (int)response.StatusCode,
                    Details: limits?.TryGetValue("retry_after", out var retry) == true ? new() { ["retry_after"] = retry } : null));
            if (bytes.Length == 0) throw new CliException("EMPTY_API_RESPONSE", "The API returned an empty response.", source);
            try
            {
                using var json = JsonDocument.Parse(bytes.ToArray());
                return new(new(json.RootElement.Clone(), source), limits);
            }
            catch (JsonException exception) { throw new CliException("INVALID_API_JSON", "The API returned malformed JSON.", source, innerException: exception); }
        }
    }

    private static Dictionary<string, string>? ReadLimits(HttpResponseMessage response)
    {
        var result = new Dictionary<string, string>();
        foreach (var field in new[] { "hourly_limit", "hourly_remaining", "hourly_reset", "daily_limit", "daily_remaining", "daily_reset", "retry_after" })
        {
            var header = field == "retry_after" ? "retry-after" : "x-rl-" + field.Replace('_', '-');
            if (response.Headers.TryGetValues(header, out var values)) result[field] = string.Join(", ", values);
        }
        return result.Count == 0 ? null : result;
    }
}
