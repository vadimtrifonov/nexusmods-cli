namespace NexusMods;

internal sealed record VersionInfo(string Id, string GameScopedId, object ModFile, string Position, string Name,
    string Version, string Category, string UploadedAt, bool Primary);
internal sealed record DependencyInspection(string Status, VersionInfo Identity, object? Raw, object? Materialized,
    List<PublicError> Errors, Dictionary<string, string>? RateLimits);

internal sealed class NexusV3(ApiHttp http)
{
    private const string Source = "nexus-v3";
    private Task<ApiResponse> Get(string path, string key) => http.Send(App.ApiOrigin + "/v3" + path, Source, key);

    private static object Mod(JsonValue mod) => new
    {
        id = mod["id"].OpaqueId(), game_scoped_id = mod["game_scoped_id"].Id(), name = mod["name"].Text(),
        game = new { id = mod["game"]["id"].OpaqueId(), name = mod["game"]["name"].Text(), domain = mod["game"]["domain_name"].Text() },
        status = mod["status"].OptionalText(), adult = mod["adult_content"].OptionalBoolean()
    };
    private static VersionInfo Version(JsonValue version) => new(
        version["id"].OpaqueId(), version["game_scoped_id"].Id(),
        new { id = version["file"]["id"].OpaqueId(), name = version["file"]["name"].Text() },
        version["position"].Decimal(), version["name"].Text(), version["version"].Text(), version["category"].Text(),
        version["uploaded_at"].IsoTime(), !version["is_primary"].IsMissing && version["is_primary"].Boolean());

    private static object Raw(JsonValue root) => new
    {
        dependency_definitions = root["dependency_definitions"].Array().Select(definition => new
        {
            id = definition["id"].OpaqueId(),
            alternatives = definition["ranges"].Array().Select(range => new
            {
                id = range["id"].OpaqueId(),
                target_mod_file = new
                {
                    id = range["target_mod_file"]["id"].OpaqueId(), name = range["target_mod_file"]["name"].Text(),
                    mod = Mod(range["target_mod_file"]["mod"])
                },
                min_version = Version(range["min_version"]),
                max_version = range["max_version"].Element.ValueKind == System.Text.Json.JsonValueKind.Null ? null : Version(range["max_version"])
            }).ToArray()
        }).ToArray(),
        dlc_dependency_definitions = root["dlc_dependency_definitions"].Array().Select(definition => new
        {
            id = definition["id"].OpaqueId(),
            alternatives = definition["dlc_targets"].Array().Select(target => new
            {
                id = target["id"].OpaqueId(), dlc_id = target["dlc_id"].OpaqueId(), name = target["name"].Text()
            }).ToArray()
        }).ToArray()
    };

    private static object Materialized(JsonValue root) => new
    {
        dependency_definitions = root["dependencies"].Array().Select(definition => new
        {
            id = definition["id"].OpaqueId(),
            candidate_mod_files = definition["candidate_mod_files"].Array().Select(file => new
            {
                id = file["id"].OpaqueId(), name = file["name"].Text(), mod = Mod(file["mod"]),
                candidate_versions = file["candidate_versions"].Array().Select(Version).ToArray()
            }).ToArray()
        }).ToArray()
    };

    internal Task<DependencyInspection> Inspect(string gameDomain, string fileId, string key) => CliException.From(Source, async () =>
    {
        var response = await Get($"/games/{Uri.EscapeDataString(gameDomain)}/mod-file-versions/{Uri.EscapeDataString(fileId)}", key);
        var identity = Version(response.Value["data"].Object());
        var path = $"/mod-file-versions/{Uri.EscapeDataString(identity.Id)}/dependencies";
        var errors = new List<PublicError>();
        var limits = response.RateLimits;
        object? raw = null, materialized = null;
        try
        {
            var result = await Get(path, key);
            limits = RateLimits.Merge(limits, result.RateLimits);
            raw = Raw(result.Value);
        }
        catch (Exception exception) { errors.Add(CliException.Public(exception, Source, "dependencies.raw")); }
        try
        {
            var result = await Get(path + "/ranges/materialized", key);
            limits = RateLimits.Merge(limits, result.RateLimits);
            materialized = Materialized(result.Value);
        }
        catch (Exception exception) { errors.Add(CliException.Public(exception, Source, "dependencies.materialized")); }
        return new DependencyInspection(errors.Count == 0 ? "ok" : "partial", identity, raw, materialized, errors, limits);
    });
}
