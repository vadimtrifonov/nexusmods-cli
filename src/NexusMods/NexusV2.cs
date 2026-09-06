using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusMods;

internal sealed record NexusGame(string Id, string Domain);
internal sealed record SearchOptions(string GameDomain, string Query, string Field, string Sort, string Direction, int Offset, int Limit);
internal sealed record SearchPage(SearchOptions Query, long TotalCount, int ReturnedCount, object[] Mods);
internal sealed record ModFile(string Uid, string FileId, string ModId, string Name, string Uri, string Version,
    string Category, bool Primary, string UploadedAt, string? SizeBytes, string? Description, bool ManagerDownload,
    string VirusScanStatus, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string[]? Changelog);
internal sealed record ModInfo(string Uid, string ModId, string Url, string Name, string? Author, object Uploader,
    string Status, object Category, object[] Tags, string Summary, string PageVersion, string CreatedAt, string UpdatedAt,
    long Endorsements, long Downloads, bool? Adult, bool DirectDownloadEnabled, bool LegacyModRequirementsEnabled,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Description);
internal sealed record ModBundle(ModInfo Mod, ModFile[] Files);
internal sealed record Requirement(string Id, string GameId, string ModId, string Name, string? Notes, string Url, bool External);
internal sealed record DlcRequirement(string Id, string GameId, string Name, string? Notes);
internal sealed record RequirementPage(long TotalCount, int ReturnedCount, Requirement[] Records);
internal sealed record ModRequirements(RequirementPage Nexus, DlcRequirement[] Dlc);
internal sealed record ContentOptions(string GameId, string ModId, string FileId, int Offset, int Limit, string? PathSubstring, string? Extension);
internal sealed record FileContents(long TotalCount, int ReturnedCount, int Offset, int Limit, object[] Entries);

internal sealed class NexusV2(ApiHttp http)
{
    private const string Source = "nexus-v2";
    private sealed record GraphResponse(JsonValue? Data, List<PublicError> Errors, Dictionary<string, string>? RateLimits);

    private const string GameQuery = """
        query ResolveGame($domainName: String!) {
          game(domainName: $domainName) { id domainName }
        }
        """;
    private const string SearchQuery = """
        query SearchMods($filter: ModsFilter!, $sort: [ModsSort!], $offset: Int!, $count: Int!) {
          mods(filter: $filter, sort: $sort, offset: $offset, count: $count) {
            totalCount nodesCount
            nodes {
              uid modId name author summary status category version updatedAt endorsements downloads adultContent
              uploader { memberId name }
            }
          }
        }
        """;
    private static string InspectQuery(bool description, bool changelog) => $$"""
        query InspectMod($gameId: ID!, $modId: ID!) {
          mod(gameId: $gameId, modId: $modId) {
            uid modId name author summary {{(description ? "description" : "")}}
            status category version createdAt updatedAt endorsements downloads adultContent
            directDownloadEnabled legacyModRequirementsEnabled
            uploader { memberId name } modCategory { categoryId name } tags { id name }
          }
          modFiles(gameId: $gameId, modId: $modId) {
            uid fileId modId name version category primary date sizeInBytes description manager scannedV2 uri
            {{(changelog ? "changelogText" : "")}}
          }
        }
        """;
    private const string RequirementsQuery = """
        query ModRequirements($gameId: ID!, $modId: ID!, $offset: Int!) {
          mod(gameId: $gameId, modId: $modId) {
            modRequirements(skipDisabledRequirements: true) {
              nexusRequirements(offset: $offset, count: 100) {
                totalCount
                nodes { id gameId modId modName notes url externalRequirement }
              }
              dlcRequirements { gameExpansion { id gameId name } notes }
            }
          }
        }
        """;
    private const string ContentsQuery = """
        query FileContents($filter: ModFileContentSearchFilter!, $offset: Int!, $count: Int!) {
          modFileContents(filter: $filter, offset: $offset, count: $count) {
            totalCount nodesCount
            nodes { id gameId modId fileId filePath fileName fileExtension fileSize }
          }
        }
        """;
    private const string HashQuery = """
        query IdentifyFiles($md5s: [String!]!) {
          fileHashes(md5s: $md5s) {
            createdAt fileName fileSize fileType gameId md5 modFileId
            modFile {
              fileId modId name version category uri sizeInBytes
              game { id domainName } mod { modId name }
            }
          }
        }
        """;

    private async Task<GraphResponse> Query(string query, object variables)
    {
        var response = await http.Send(App.ApiOrigin + "/v2/graphql", Source, body: new { query, variables });
        var root = response.Value.Object();
        var errors = new List<PublicError>();
        if (!root["errors"].IsNull)
            foreach (var item in root["errors"].Array())
            {
                var error = item.Object();
                var message = error["message"].Element.ValueKind == JsonValueKind.String
                    ? error["message"].Text() : "The GraphQL operation failed.";
                var path = error["path"].Element;
                var details = path.ValueKind == JsonValueKind.Array
                    ? new Dictionary<string, object?> { ["path"] = path.EnumerateArray()
                        .Where(part => part.ValueKind is JsonValueKind.String or JsonValueKind.Number).ToArray() }
                    : null;
                errors.Add(new("GRAPHQL_ERROR", message, Source, Details: details));
            }
        return new(root["data"].IsNull ? null : root["data"].Object(), errors, response.RateLimits);
    }

    private static object Uploader(JsonValue value) => new { id = value["memberId"].Id(), name = value["name"].Text() };
    private static string? Notes(JsonValue value) => value.OptionalText() is { } text ? Markup.Text(text) : null;
    private static Dictionary<string, object> Filter(string field, string op, object value) => new() { [field] = new[] { new { op, value } } };

    internal Task<Evidence<NexusGame>> ResolveGame(string gameDomain) => CliException.From(Source, async () =>
    {
        var response = await Query(GameQuery, new { domainName = gameDomain });
        if (response.Data is not { } data || data["game"].IsNull)
            return new Evidence<NexusGame>(null, response.Errors, response.RateLimits);
        var game = data["game"].Object();
        var domain = game["domainName"].Text();
        if (!domain.Equals(gameDomain, StringComparison.OrdinalIgnoreCase))
            throw new CliException("GAME_DOMAIN_MISMATCH", "Nexus returned metadata for a different game.", Source);
        return new Evidence<NexusGame>(new(game["id"].Id(), domain), response.Errors, response.RateLimits);
    });

    internal Task<Evidence<SearchPage>> Search(SearchOptions request) => CliException.From(Source, async () =>
    {
        var field = request.Field == "name-stemmed" ? "nameStemmed" : request.Field;
        var sort = request.Sort switch { "updated" => "updatedAt", "created" => "createdAt", _ => request.Sort };
        var response = await Query(SearchQuery, new
        {
            filter = new { op = "AND", filter = new[] { Filter("gameDomainName", "EQUALS", request.GameDomain),
                Filter(field, request.Field == "name" ? "EQUALS" : "MATCHES", request.Query) } },
            sort = new[] { new Dictionary<string, object> { [sort] = new { direction = request.Direction.ToUpperInvariant() } } },
            offset = request.Offset, count = request.Limit
        });
        if (response.Data is not { } data || data["mods"].IsNull) return new Evidence<SearchPage>(null, response.Errors, response.RateLimits);
        var page = data["mods"].Object();
        var mods = page["nodes"].Array().Select(mod => (object)new
        {
            mod_id = mod["modId"].Id(), uid = mod["uid"].Id(), url = App.ModUrl(mod["modId"].Id(), request.GameDomain),
            name = mod["name"].Text(), author = mod["author"].OptionalText(), uploader = Uploader(mod["uploader"]),
            summary = mod["summary"].Text(), status = mod["status"].Text(), category = mod["category"].Text(),
            page_version = mod["version"].Text(), updated_at = mod["updatedAt"].IsoTime(),
            endorsements = mod["endorsements"].Integer(), downloads = mod["downloads"].Integer(), adult = mod["adultContent"].OptionalBoolean()
        }).ToArray();
        return new Evidence<SearchPage>(new(request, page["totalCount"].Integer(), mods.Length, mods), response.Errors, response.RateLimits);
    });

    internal Task<Evidence<ModBundle>> Inspect(NexusGame game, string modId, bool description, bool changelog) => CliException.From(Source, async () =>
    {
        var response = await Query(InspectQuery(description, changelog), new { gameId = game.Id, modId });
        if (response.Data is not { } data || data["mod"].IsNull || data["modFiles"].IsNull)
            return new Evidence<ModBundle>(null, response.Errors, response.RateLimits);
        var source = data["mod"].Object();
        var id = source["modId"].Id();
        var category = source["modCategory"];
        var mod = new ModInfo(
            Uid: source["uid"].Id(), ModId: id, Url: App.ModUrl(id, game.Domain), Name: source["name"].Text(),
            Author: source["author"].OptionalText(), Uploader: Uploader(source["uploader"]), Status: source["status"].Text(),
            Category: new { id = category.IsNull ? null : category["categoryId"].Id(), name = category.IsNull ? source["category"].Text() : category["name"].Text() },
            Tags: source["tags"].Array().Select(tag => (object)new { id = tag["id"].Id(), name = tag["name"].Text() }).ToArray(),
            Summary: source["summary"].Text(), PageVersion: source["version"].Text(), CreatedAt: source["createdAt"].IsoTime(),
            UpdatedAt: source["updatedAt"].IsoTime(), Endorsements: source["endorsements"].Integer(), Downloads: source["downloads"].Integer(),
            Adult: source["adultContent"].OptionalBoolean(), DirectDownloadEnabled: source["directDownloadEnabled"].Boolean(),
            LegacyModRequirementsEnabled: source["legacyModRequirementsEnabled"].Boolean(),
            Description: description ? new { text = Markup.Text(source["description"].Text()), source_url = App.ModUrl(id, game.Domain) } : null);
        var files = data["modFiles"].Array().Select(file => new ModFile(
            Uid: file["uid"].Id(), FileId: file["fileId"].Id(), ModId: file["modId"].Id(), Name: file["name"].Text(),
            Uri: file["uri"].Text(), Version: file["version"].Text(), Category: file["category"].Text(),
            Primary: file["primary"].Integer() != 0, UploadedAt: file["date"].UnixTime(), SizeBytes: file["sizeInBytes"].OptionalId(),
            Description: Notes(file["description"]), ManagerDownload: file["manager"].Integer() != 0, VirusScanStatus: file["scannedV2"].Text(),
            Changelog: changelog ? file["changelogText"].Array().Select(entry => entry.Text()).ToArray() : null)).ToArray();
        return new Evidence<ModBundle>(new(mod, files), response.Errors, response.RateLimits);
    });

    internal async Task<Evidence<ModRequirements>> Requirements(string gameId, string modId)
    {
        ModRequirements? data = null;
        var errors = new List<PublicError>();
        Dictionary<string, string>? limits = null;
        do
        {
            try
            {
                var response = await Query(RequirementsQuery, new { gameId, modId, offset = data?.Nexus.ReturnedCount ?? 0 });
                errors.AddRange(response.Errors);
                limits = RateLimits.Merge(limits, response.RateLimits);
                if (response.Data is not { } root || root["mod"].IsNull)
                {
                    if (response.Errors.Count == 0) errors.Add(new("GRAPHQL_PRIMARY_RESULT_MISSING", "Nexus did not return the mod's declared requirements.", Source));
                    break;
                }
                var requirements = root["mod"]["modRequirements"].Object();
                var nexus = requirements["nexusRequirements"].Object();
                var total = nexus["totalCount"].Integer();
                var records = nexus["nodes"].Array().Select(entry => new Requirement(entry["id"].OpaqueId(), entry["gameId"].Id(),
                    entry["modId"].Id(), entry["modName"].Text(), Notes(entry["notes"]), entry["url"].Text(), entry["externalRequirement"].Boolean())).ToArray();
                var dlc = requirements["dlcRequirements"].Array().Select(entry => new DlcRequirement(entry["gameExpansion"]["id"].OpaqueId(),
                    entry["gameExpansion"]["gameId"].Id(), entry["gameExpansion"]["name"].Text(), Notes(entry["notes"]))).ToArray();
                Requirement[] all = [.. data?.Nexus.Records ?? [], .. records];
                data = new(new(total, all.Length, all), dlc);
                if (records.Length == 0 && all.Length < total)
                    throw new CliException("REQUIREMENTS_INCOMPLETE", "Nexus returned an empty requirement page before its reported total.");
            }
            catch (Exception exception) { errors.Add(CliException.Public(exception, Source)); break; }
        } while (data.Nexus.ReturnedCount < data.Nexus.TotalCount);
        return new(data, errors, limits);
    }

    private static int ContentFilterId(string text, string label)
    {
        if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id < 1)
            throw new CliException("INVALID_ARGUMENT", $"{label} must be between 1 and 2147483647 for Nexus content filters.");
        return id;
    }

    internal Task<Evidence<FileContents>> Contents(ContentOptions request) => CliException.From(Source, async () =>
    {
        var filters = new List<Dictionary<string, object>>
        {
            Filter("gameId", "EQUALS", ContentFilterId(request.GameId, "game ID")),
            Filter("modId", "EQUALS", ContentFilterId(request.ModId, "mod ID")),
            Filter("fileId", "EQUALS", ContentFilterId(request.FileId, "file ID"))
        };
        // Despite the WILDCARD operator, Nexus expects a literal substring, not a glob.
        if (request.PathSubstring is not null) filters.Add(Filter("filePathWildcard", "WILDCARD", request.PathSubstring));
        if (request.Extension is not null)
            filters.Add(Filter("fileExtensionExact", "EQUALS", "." + (request.Extension.StartsWith('.') ? request.Extension[1..] : request.Extension)));
        var response = await Query(ContentsQuery, new { filter = new { op = "AND", filter = filters }, offset = request.Offset, count = request.Limit });
        if (response.Data is not { } data || data["modFileContents"].IsNull)
            return new Evidence<FileContents>(null, response.Errors, response.RateLimits);
        var page = data["modFileContents"].Object();
        var entries = page["nodes"].Array().Select(entry => (object)new
        {
            id = entry["id"].Text(), game_id = entry["gameId"].Id(), mod_id = entry["modId"].Id(), file_id = entry["fileId"].Id(),
            path = entry["filePath"].Text(), name = entry["fileName"].Text(), extension = entry["fileExtension"].Text(), size_bytes = entry["fileSize"].Id()
        }).ToArray();
        return new Evidence<FileContents>(new(page["totalCount"].Integer(), entries.Length, request.Offset, request.Limit, entries), response.Errors, response.RateLimits);
    });

    internal Task<Evidence<object[]>> Identify(string md5) => CliException.From(Source, async () =>
    {
        var response = await Query(HashQuery, new { md5s = new[] { md5 } });
        if (response.Data is not { } data || data["fileHashes"].IsNull)
            return new Evidence<object[]>(null, response.Errors, response.RateLimits);
        var associations = data["fileHashes"].Array().Select(hash =>
        {
            object? linked = null;
            if (!hash["modFile"].IsNull)
            {
                var file = hash["modFile"].Object();
                var mod = file["mod"].Object();
                var game = file["game"].Object();
                linked = new
                {
                    game_id = game["id"].Id(), game_domain = game["domainName"].Text(), mod_id = mod["modId"].Id(), mod_name = mod["name"].Text(),
                    mod_url = App.ModUrl(mod["modId"].Id(), game["domainName"].Text()), file_id = file["fileId"].Id(), display_name = file["name"].Text(),
                    uri = file["uri"].Text(), version = file["version"].Text(), category = file["category"].Text(), size_bytes = file["sizeInBytes"].OptionalId()
                };
            }
            return (object)new
            {
                md5 = hash["md5"].Text().ToLowerInvariant(), uploaded_archive_name = hash["fileName"].Text(), uploaded_size_bytes = hash["fileSize"].Id(),
                file_type = hash["fileType"].Text(), game_id = hash["gameId"].Id(), file_id = hash["modFileId"].Id(), indexed_at = hash["createdAt"].IsoTime(), nexus_file = linked
            };
        }).ToArray();
        return new Evidence<object[]>(associations, response.Errors, response.RateLimits);
    });
}
