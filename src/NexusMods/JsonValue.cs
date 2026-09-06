using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NexusMods;

// A narrow reader for Nexus's mixed string/number identifiers and source-attributed schema errors.
internal readonly struct JsonValue(JsonElement value, string path)
{
    internal JsonElement Element => value;
    internal bool IsNull => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined;
    internal bool IsMissing => value.ValueKind == JsonValueKind.Undefined;
    internal JsonValue this[string name] => Object().Element.TryGetProperty(name, out var child)
        ? new(child, $"{path}.{name}") : new(default, $"{path}.{name}");

    private CliException Expected(string kind, Exception? innerException = null) => new("UPSTREAM_SCHEMA_ERROR", $"Expected {kind} at {path}.", innerException: innerException);
    internal JsonValue Object() => value.ValueKind == JsonValueKind.Object ? this : throw Expected("an object");
    internal JsonValue[] Array()
    {
        if (value.ValueKind != JsonValueKind.Array) throw Expected("an array");
        var sourcePath = path;
        return value.EnumerateArray().Select((item, i) => new JsonValue(item, $"{sourcePath}[{i}]")).ToArray();
    }
    internal string Text() => value.ValueKind == JsonValueKind.String ? value.GetString()! : throw Expected("a string");
    internal string? OptionalText() => IsNull ? null : Text();
    internal bool Boolean() => value.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? value.GetBoolean() : throw Expected("a boolean");
    internal bool? OptionalBoolean() => IsNull ? null : Boolean();
    internal long Integer() => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number)
        ? number : throw Expected("an integer");
    internal string Id()
    {
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.Number => value.GetRawText(),
            _ => ""
        };
        return Regex.IsMatch(text, "^[0-9]+$") ? text : throw Expected("a non-negative decimal identifier");
    }
    internal string? OptionalId() => IsNull ? null : Id();
    internal string OpaqueId() => value.ValueKind == JsonValueKind.String && Text().Length > 0 ? Text() : Id();
    internal string Decimal()
    {
        if (value.ValueKind == JsonValueKind.Number) return Integer().ToString(CultureInfo.InvariantCulture);
        var text = Text();
        return Regex.IsMatch(text, @"^-?[0-9]+(?:\.[0-9]+)?$") ? text : throw Expected("a decimal string");
    }
    internal string IsoTime() => DateTimeOffset.TryParse(Text(), CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal, out var time) ? App.Timestamp(time) : throw Expected("an ISO-8601 timestamp");
    internal string UnixTime()
    {
        try { return App.Timestamp(DateTimeOffset.FromUnixTimeSeconds(Integer())); }
        catch (ArgumentOutOfRangeException exception) { throw Expected("a Unix timestamp", exception); }
    }
}
