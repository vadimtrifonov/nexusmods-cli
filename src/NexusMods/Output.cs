using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusMods;

internal static class App
{
    internal const string Name = "nexusmods";
    internal const string ApiOrigin = "https://api.nexusmods.com";
    internal static readonly string Version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
    internal static string UserAgent => $"{Name}/{Version}";
    internal static string ModUrl(string modId, string domain) => $"https://www.nexusmods.com/{Uri.EscapeDataString(domain)}/mods/{modId}";
    internal static string Timestamp(DateTimeOffset value) => value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };
}

internal sealed record PublicError(
    string Code,
    string Message,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Section = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? HttpStatus = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, object?>? Details = null);

internal sealed class CliException(PublicError error, Exception? innerException = null) : Exception(error.Message, innerException)
{
    internal PublicError Error { get; } = error;
    internal CliException(string code, string message, string? source = null, Dictionary<string, object?>? details = null, Exception? innerException = null)
        : this(new PublicError(code, message, source, Details: details), innerException) { }

    internal static PublicError Public(Exception exception, string? source = null, string? section = null)
    {
        var error = exception is CliException cli ? cli.Error : new PublicError("INTERNAL_ERROR", "The command failed unexpectedly.");
        return error with { Source = error.Source ?? source, Section = section ?? error.Section };
    }

    internal static async Task<T> From<T>(string source, Func<Task<T>> operation)
    {
        try { return await operation(); }
        catch (Exception exception) { throw new CliException(Public(exception, source), exception); }
    }
}

internal sealed record ResultMeta(string RetrievedAt, string[] Sources,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Dictionary<string, string>? RateLimits);

internal sealed record CommandResult(string Status,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Data,
    IReadOnlyList<PublicError> Errors, ResultMeta Meta)
{
    internal static CommandResult Success(object data, IEnumerable<string> sources,
        IReadOnlyList<PublicError>? errors = null, Dictionary<string, string>? limits = null) =>
        new(errors is { Count: > 0 } ? "partial" : "complete", data, errors ?? [],
            new(App.Timestamp(DateTimeOffset.UtcNow), sources.Distinct().ToArray(), limits));

    internal static CommandResult Failed(IReadOnlyList<PublicError> errors, IEnumerable<string> sources,
        Dictionary<string, string>? limits = null) =>
        new("failed", null, errors, new(App.Timestamp(DateTimeOffset.UtcNow), sources.Distinct().ToArray(), limits));
}

internal sealed record Evidence<T>(T? Data, List<PublicError> Errors, Dictionary<string, string>? RateLimits) where T : class;

internal static class RateLimits
{
    internal static Dictionary<string, string>? Merge(params IEnumerable<Dictionary<string, string>?> values)
    {
        var result = new Dictionary<string, string>();
        foreach (var value in values)
            if (value is not null)
                foreach (var (key, text) in value) result[key] = text;
        return result.Count == 0 ? null : result;
    }
}
