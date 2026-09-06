using System.Net;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusMods.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task SearchMatchesTheEstablishedContractAndExplicitFilterSemantics()
    {
        foreach (var domain in new[] { "skyrimspecialedition", "fallout4" })
        {
            var calls = 0;
            using var client = new HttpClient(new StubHttp(async request =>
            {
                calls++;
                Assert.False(request.Headers.Contains("apikey"));
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                var filter = body["variables"]!["filter"]!["filter"]!;
                Assert.Equal(domain, (string?)filter[0]!["gameDomainName"]![0]!["value"]);
                Assert.Equal("EQUALS", (string?)filter[1]!["name"]![0]!["op"]);
                Assert.Equal("ASC", (string?)body["variables"]!["sort"]![0]!["name"]!["direction"]);
                return Fixtures.Json(Fixtures.Load("search"));
            }));
            var result = await Fixtures.Run(Fixtures.Commands(client), "search", "--game", domain, "--query", " SkyUI ", "--field", "name");
            if (domain == Fixtures.Skyrim.Domain) Fixtures.Golden("search", result);
            Assert.Equal("partial", result.Status);
            var data = Fixtures.Node(result.Data!);
            Assert.Equal(domain, (string?)data["query"]!["game_domain"]);
            Assert.Equal($"https://www.nexusmods.com/{domain}/mods/12604", (string?)data["mods"]![0]!["url"]);
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task InspectionPreservesRequirementsAndGenuineOptInFields()
    {
        var includeChangelog = true;
        using var client = new HttpClient(new StubHttp(async request =>
        {
            var query = (string?)JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["query"];
            Assert.False(request.Headers.Contains("apikey"));
            if (query!.Contains("query ResolveGame")) return Fixtures.Game(Fixtures.Skyrim);
            if (query.Contains("query ModRequirements"))
            {
                Assert.Contains("skipDisabledRequirements: true", query);
                Assert.DoesNotContain("modsRequiringThisMod", query);
                return Fixtures.Json(Fixtures.Load("requirements"));
            }
            Assert.Equal(includeChangelog, query.Contains("changelogText"));
            var catalog = Fixtures.Load("catalog");
            if (!includeChangelog) catalog["data"]!["modFiles"]![0]!["changelogText"] = new JsonObject();
            return Fixtures.Json(catalog);
        }));
        var commands = Fixtures.Commands(client);
        var result = await Fixtures.Run(commands, "inspect", "--game=skyrimspecialedition", "--mod", "12604", "--file", "749043", "--description", "--changelog");
        Fixtures.Golden("inspect", result);
        includeChangelog = false;
        var plain = Fixtures.Node(await Fixtures.Run(commands, "inspect", "--game=skyrimspecialedition", "--mod", "12604"));
        Assert.Null(plain["data"]!["mod"]!["description"]);
        Assert.Null(plain["data"]!["files"]!["records"]![0]!["changelog"]);
        Assert.Null(plain["data"]!["files"]!["records"]![0]!["archive_name"]);
        Assert.Equal("storage/fixture", (string?)plain["data"]!["files"]!["records"]![0]!["uri"]);
    }

    [Fact]
    public async Task InspectionResolvesEachGameOnceAndUsesItAcrossAllScopedRequests()
    {
        var selected = Fixtures.Skyrim;
        var queries = new List<string>();
        using var client = new HttpClient(new StubHttp(async request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.StartsWith("/v3/"))
            {
                Assert.True(request.Headers.Contains("apikey"));
                if (path.EndsWith("/ranges/materialized")) return Fixtures.Json(Fixtures.Load("materialized"));
                if (path.EndsWith("/dependencies")) return Fixtures.Json(Fixtures.Load("raw"));
                Assert.Equal($"/v3/games/{selected.Domain}/mod-file-versions/749043", path);
                var identity = Fixtures.Load("identity");
                identity["data"]!["game_scoped_id"] = "749043";
                return Fixtures.Json(identity);
            }
            Assert.False(request.Headers.Contains("apikey"));
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            var query = (string)body["query"]!;
            var variables = body["variables"]!;
            queries.Add(query);
            if (query.Contains("query ResolveGame"))
            {
                Assert.Equal(selected.Domain, (string?)variables["domainName"]);
                var response = Fixtures.Json(new
                {
                    data = new { game = new { id = selected.Id, domainName = selected.Domain } },
                    errors = selected.Domain == "fallout4" ? new[] { new { message = "Game lookup warning", path = new[] { "game" } } } : []
                });
                response.Headers.Add("X-RL-HOURLY-REMAINING", "123");
                return response;
            }
            if (query.Contains("query FileContents"))
            {
                Assert.Equal(int.Parse(selected.Id), (int)variables["filter"]!["filter"]![0]!["gameId"]![0]!["value"]!);
                return Fixtures.Json(new { data = new { modFileContents = new { totalCount = 1, nodes = new[]
                {
                    new { id = "entry", gameId = selected.Id, modId = "12604", fileId = "749043", filePath = "fixture.esp", fileName = "fixture.esp", fileExtension = ".esp", fileSize = "1" }
                } } } });
            }
            Assert.Equal(selected.Id, (string?)variables["gameId"]);
            if (query.Contains("query ModRequirements")) return Fixtures.Json(Fixtures.Load("requirements"));
            Assert.Contains("query InspectMod", query);
            var catalog = Fixtures.Load("catalog");
            catalog["data"]!["mod"]!["legacyModRequirementsEnabled"] = false;
            return Fixtures.Json(catalog);
        }));
        var commands = Fixtures.Commands(client, () => "fake-key");
        foreach (var game in new[] { Fixtures.Skyrim, new NexusGame("1151", "fallout4") })
        {
            selected = game;
            queries.Clear();
            var result = await Fixtures.Run(commands, "inspect", "--game", game.Domain, "--mod=12604", "--file=749043", "--description", "--contents");
            Assert.Equal(game.Domain == "fallout4" ? "partial" : "complete", result.Status);
            Assert.Equal("123", result.Meta.RateLimits!["hourly_remaining"]);
            if (result.Status == "partial") Assert.Equal("Game lookup warning", Assert.Single(result.Errors).Message);
            var data = Fixtures.Node(result.Data!);
            Assert.Equal($"https://www.nexusmods.com/{game.Domain}/mods/12604", (string?)data["mod"]!["url"]);
            Assert.Equal((string?)data["mod"]!["url"], (string?)data["mod"]!["description"]!["source_url"]);
            Assert.Equal(game.Id, (string?)data["contents"]!["entries"]![0]!["game_id"]);
            Assert.Equal("ok", (string?)data["dependencies"]!["status"]);
            Assert.Equal("1704", (string?)data["author_declared_requirements"]!["nexus"]!["records"]![0]!["game_id"]);
            Assert.Single(queries, query => query.Contains("query ResolveGame"));
        }
    }

    [Fact]
    public async Task InspectionRequestFailuresRetainGameLookupEvidence()
    {
        foreach (var failure in new[] { "http", "schema", "transport" })
        foreach (var withLookupErrors in new[] { false, true })
        {
            var calls = 0;
            using var client = new HttpClient(new StubHttp(async request =>
            {
                calls++;
                Assert.False(request.Headers.Contains("apikey"));
                var query = (string)JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["query"]!;
                if (calls == 1)
                {
                    Assert.Contains("query ResolveGame", query);
                    var response = Fixtures.Json(new
                    {
                        data = new { game = new { id = 1151, domainName = "fallout4" } },
                        errors = withLookupErrors ? new[] { new { message = "Game lookup warning", path = new[] { "game" } } } : []
                    });
                    response.Headers.Add("X-RL-HOURLY-REMAINING", "123");
                    return response;
                }
                Assert.Contains("query InspectMod", query);
                return failure switch
                {
                    "http" => new(HttpStatusCode.ServiceUnavailable),
                    "schema" => Fixtures.Json(new { data = new { mod = new { }, modFiles = Array.Empty<object>() } }),
                    _ => throw new IOException("fake-secret")
                };
            }));
            var result = await Fixtures.Run(Fixtures.Commands(client), "inspect", "--game=fallout4", "--mod=1");
            Assert.Equal("failed", result.Status);
            Assert.Null(result.Data);
            Assert.Equal(new[] { "nexus-v2" }, result.Meta.Sources);
            Assert.NotNull(result.Meta.RateLimits);
            Assert.Equal("123", result.Meta.RateLimits["hourly_remaining"]);
            Assert.Equal(withLookupErrors ? 2 : 1, result.Errors.Count);
            Assert.All(result.Errors, error => Assert.Equal("nexus-v2", error.Source));
            if (withLookupErrors)
            {
                Assert.Equal("Game lookup warning", result.Errors[0].Message);
                Assert.Equal("game", (string?)Fixtures.Node(result.Errors[0])["details"]!["path"]![0]);
            }
            Assert.Equal(failure switch
            {
                "http" => "HTTP_503", "schema" => "UPSTREAM_SCHEMA_ERROR", _ => "API_REQUEST_FAILED"
            }, result.Errors[^1].Code);
            Assert.DoesNotContain("fake-secret", Fixtures.Node(result).ToJsonString());
            Assert.Equal(2, calls);
        }
    }

    [Fact]
    public async Task GameResolutionFailuresStopInspectionWithoutUsingAnotherCatalog()
    {
        foreach (var failure in new[] { "missing", "graphql", "schema", "mismatch" })
        {
            var calls = 0;
            using var client = new HttpClient(new StubHttp(async request =>
            {
                calls++;
                Assert.False(request.Headers.Contains("apikey"));
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                Assert.Contains("query ResolveGame", (string)body["query"]!);
                Assert.Equal("fallout4", (string?)body["variables"]!["domainName"]);
                var payload = failure switch
                {
                    "missing" => (object)new { data = new { game = (object?)null } },
                    "graphql" => new { data = new { game = (object?)null }, errors = new[] { new { message = "Game unavailable", path = new[] { "game" } } } },
                    "schema" => new { data = new { game = new { id = (string?)null, domainName = "fallout4" } } },
                    _ => new { data = new { game = new { id = "1704", domainName = "skyrimspecialedition" } } }
                };
                var response = Fixtures.Json(payload);
                response.Headers.Add("X-RL-HOURLY-REMAINING", "8");
                return response;
            }));
            var result = await Fixtures.Run(Fixtures.Commands(client), "inspect", "--game=fallout4", "--mod=1");
            Assert.Equal("failed", result.Status);
            Assert.Null(result.Data);
            var error = Assert.Single(result.Errors);
            Assert.Equal("nexus-v2", error.Source);
            Assert.Equal(failure switch
            {
                "missing" => "GRAPHQL_PRIMARY_RESULT_MISSING", "graphql" => "GRAPHQL_ERROR",
                "schema" => "UPSTREAM_SCHEMA_ERROR", _ => "GAME_DOMAIN_MISMATCH"
            }, error.Code);
            if (failure is "missing" or "graphql") Assert.Equal("8", result.Meta.RateLimits!["hourly_remaining"]);
            if (failure == "graphql") Assert.Equal("game", (string?)Fixtures.Node(error)["details"]!["path"]![0]);
            Assert.Equal(1, calls);
        }
    }

    [Fact]
    public async Task RequirementPagesPreserveDeclarationsAndEarlierEvidenceOnFailure()
    {
        foreach (var failure in new[] { "none", "graphql", "empty" })
        {
            var offsets = new List<int>();
            using var client = new HttpClient(new StubHttp(async request =>
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                var offset = (int)body["variables"]!["offset"]!;
                offsets.Add(offset);
                Assert.False(request.Headers.Contains("apikey"));
                if (offset > 0 && failure == "graphql") return Fixtures.Json(new { data = (object?)null, errors = new[] { new { message = "Page failed", path = new[] { "mod", "modRequirements" } } } });
                var page = Fixtures.Load("requirements");
                var nodes = page["data"]!["mod"]!["modRequirements"]!["nexusRequirements"]!;
                nodes["nodes"] = new JsonArray(nodes["nodes"]!.AsArray().Skip(offset).Take(failure == "empty" && offset > 0 ? 0 : 2).Select(node => node!.DeepClone()).ToArray());
                var response = Fixtures.Json(page);
                response.Headers.Add("x-rl-hourly-remaining", (12 - offsets.Count).ToString());
                return response;
            }));
            var result = await new NexusV2(new(client)).Requirements(Fixtures.Skyrim.Id, "1");
            Assert.Equal([0, 2], offsets);
            Assert.NotNull(result.Data);
            Assert.Equal(3, result.Data.Nexus.TotalCount);
            Assert.Equal(failure == "none" ? 3 : 2, result.Data.Nexus.ReturnedCount);
            Assert.Equal("[1, 1704]", result.Data.Nexus.Records[0].Id);
            Assert.Equal("Version 2.0 required. Ignore the obsolete guide.", result.Data.Nexus.Records[0].Notes);
            Assert.Equal("https://example.test/tool", result.Data.Nexus.Records[1].Url);
            Assert.True(result.Data.Nexus.Records[1].External);
            Assert.Equal("0", result.Data.Nexus.Records[1].GameId);
            Assert.Equal("73,1704", result.Data.Dlc[0].Id);
            if (failure == "none") { Assert.Empty(result.Errors); Assert.Equal("110", result.Data.Nexus.Records[2].GameId); }
            else Assert.Equal(failure == "empty" ? "REQUIREMENTS_INCOMPLETE" : "GRAPHQL_ERROR", Assert.Single(result.Errors).Code);
        }
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("graphql")]
    [InlineData("schema")]
    public async Task EmptyOrUnavailableRequirementsDoNotEraseModEvidence(string kind)
    {
        using var client = new HttpClient(new StubHttp(async request =>
        {
            var query = (string?)JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["query"];
            if (query!.Contains("query ResolveGame")) return Fixtures.Game(Fixtures.Skyrim);
            if (!query.Contains("query ModRequirements")) return Fixtures.Json(Fixtures.Load("catalog"));
            return Fixtures.Json(kind switch
            {
                "empty" => new { data = new { mod = new { modRequirements = new { nexusRequirements = new { totalCount = 0, nodes = Array.Empty<object>() }, dlcRequirements = Array.Empty<object>() } } } },
                "graphql" => (object)new { data = (object?)null, errors = new[] { new { message = "Requirements unavailable" } } },
                _ => new { data = new { mod = new { modRequirements = new { } } } }
            });
        }));
        var result = await Fixtures.Run(Fixtures.Commands(client), "inspect", "--game=skyrimspecialedition", "--mod", "12604");
        var data = Fixtures.Node(result.Data!);
        Assert.Equal(kind == "empty" ? "complete" : "partial", result.Status);
        Assert.Equal(1, (int)data["files"]!["returned_count"]!);
        Assert.Equal(kind == "empty" ? "ok" : "error", (string?)data["author_declared_requirements"]!["status"]);
        if (kind != "empty") Assert.Equal("author_declared_requirements", Assert.Single(result.Errors).Section);
    }

    [Fact]
    public async Task ContentFiltersUseDottedExtensionsLiteralPathsAndCheckedInt32Ids()
    {
        var calls = 0;
        using var client = new HttpClient(new StubHttp(async request =>
        {
            calls++;
            var filter = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!["variables"]!["filter"]!["filter"]!;
            Assert.Equal(1704, (int)filter[0]!["gameId"]![0]!["value"]!);
            Assert.Equal(int.MaxValue, (int)filter[1]!["modId"]![0]!["value"]!);
            Assert.Equal(int.MaxValue, (int)filter[2]!["fileId"]![0]!["value"]!);
            Assert.Equal("meshes/", (string?)filter[3]!["filePathWildcard"]![0]!["value"]);
            Assert.Equal(".nif", (string?)filter[4]!["fileExtensionExact"]![0]!["value"]);
            return Fixtures.Json(new { data = new { modFileContents = new { totalCount = 0, nodes = Array.Empty<object>() } } });
        }));
        var v2 = new NexusV2(new(client));
        foreach (var id in new[] { "", "bad", "0", "-1", "+1", " 1", "1 ", "1.0", "2147483648", "9007199254740993" })
        {
            await Fixtures.Error("INVALID_ARGUMENT", "nexus-v2", () => v2.Contents(new(id, "1", "2", 0, 10, null, null)));
            await Fixtures.Error("INVALID_ARGUMENT", "nexus-v2", () => v2.Contents(new(Fixtures.Skyrim.Id, id, "2", 0, 10, null, null)));
            await Fixtures.Error("INVALID_ARGUMENT", "nexus-v2", () => v2.Contents(new(Fixtures.Skyrim.Id, "1", id, 0, 10, null, null)));
        }
        Assert.Equal(0, calls);
        foreach (var extension in new[] { "nif", ".nif" }) await v2.Contents(new(Fixtures.Skyrim.Id, "2147483647", "2147483647", 0, 10, "meshes/", extension));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task V3PreservesAlternativeGroupsAndEndpointSpecificEnvelopes()
    {
        var materializedFails = false;
        using var client = Fixtures.Client(request =>
        {
            Assert.Equal("fake-key", Assert.Single(request.Headers.GetValues("apikey")));
            var url = request.RequestUri!.AbsolutePath;
            if (url.EndsWith("/ranges/materialized")) return materializedFails ? new(HttpStatusCode.ServiceUnavailable) : Fixtures.Json(Fixtures.Load("materialized"));
            return Fixtures.Json(Fixtures.Load(url.EndsWith("/dependencies") ? "raw" : "identity"));
        });
        var v3 = new NexusV3(new(client));
        var result = await v3.Inspect(Fixtures.Skyrim.Domain, "123", "fake-key");
        Fixtures.Golden("dependencies", new { result.Status, result.Identity, result.Raw, result.Materialized });
        Assert.False(result.Identity.Primary);
        Assert.Empty(result.Errors);
        materializedFails = true;
        var partial = await v3.Inspect(Fixtures.Skyrim.Domain, "123", "fake-key");
        Assert.Equal("partial", partial.Status);
        Assert.NotNull(partial.Raw);
        Assert.Null(partial.Materialized);
        Assert.Equal("dependencies.materialized", Assert.Single(partial.Errors).Section);
    }

    [Fact]
    public async Task IdentificationRetainsConflictsOrphansAndEmptyResults()
    {
        using var directory = new TemporaryDirectory();
        var path = directory.File("fixture.zip");
        await File.WriteAllTextAsync(path, "fixture bytes");
        var payload = Fixtures.Load("identify");
        using var client = new HttpClient(new StubHttp(async request =>
        {
            Assert.False(request.Headers.Contains("apikey"));
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
            Assert.Equal("md5s", Assert.Single(body["variables"]!.AsObject()).Key);
            return Fixtures.Json(payload);
        }));
        var commands = Fixtures.Commands(client);
        var result = await commands.Execute(new IdentifyCommand(path));
        Fixtures.Golden("identify", result);
        var nodes = payload["data"]!["fileHashes"]!.AsArray();
        var crossGame = nodes[0]!.DeepClone();
        crossGame["gameId"] = 1151;
        crossGame["modFile"]!["game"]!["id"] = 1151;
        crossGame["modFile"]!["game"]!["domainName"] = "fallout4";
        nodes.Add(crossGame);
        var all = Fixtures.Node((await Fixtures.Run(commands, "identify", "--path", path)).Data!)["associations"]!.AsArray();
        Assert.Equal(4, all.Count);
        Assert.Equal("1704", (string?)all[0]!["game_id"]);
        Assert.Equal("1151", (string?)all[3]!["game_id"]);
        Assert.Equal("https://www.nexusmods.com/fallout4/mods/10", (string?)all[3]!["nexus_file"]!["mod_url"]);
        nodes.RemoveAt(0); nodes.RemoveAt(0);
        var mismatch = Fixtures.Node((await commands.Execute(new IdentifyCommand(path))).Data!);
        Assert.Equal("1", (string?)mismatch["associations"]![0]!["uploaded_size_bytes"]);
        nodes.Clear();
        Assert.Empty(Fixtures.Node((await commands.Execute(new IdentifyCommand(path))).Data!)["associations"]!.AsArray());
    }

    [Fact]
    public async Task FatalGraphQlErrorsRetainPathsSourcesAndRateLimits()
    {
        using var directory = new TemporaryDirectory();
        await File.WriteAllTextAsync(directory.File("fixture.zip"), "fixture bytes");
        foreach (var args in new string[][]
        {
            ["search", "--game=skyrimspecialedition", "--query", "fixture", "--field", "name"],
            ["inspect", "--game=skyrimspecialedition", "--mod", "1"], ["identify", "--path", directory.File("fixture.zip")]
        })
        foreach (var withErrors in new[] { false, true })
        {
            using var client = new HttpClient(new StubHttp(async request =>
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync())!;
                if (((string?)body["query"])!.Contains("query ResolveGame")) return Fixtures.Game(Fixtures.Skyrim);
                var response = Fixtures.Json(new { data = (object?)null, errors = withErrors
                    ? new object[] { new { message = "Resolver failed", path = new object[] { "field", 0 } }, new { message = "Additional error" } } : [] });
                response.Headers.Add("X-RL-HOURLY-REMAINING", "123");
                return response;
            }));
            var result = await Fixtures.Run(Fixtures.Commands(client), args);
            Assert.Equal("failed", result.Status);
            Assert.Null(result.Data);
            Assert.Equal("123", result.Meta.RateLimits!["hourly_remaining"]);
            Assert.Equal(args[0] == "identify" ? new[] { "local-file", "nexus-v2" } : ["nexus-v2"], result.Meta.Sources);
            Assert.Equal(withErrors ? 2 : 1, result.Errors.Count);
            Assert.Equal(withErrors ? "GRAPHQL_ERROR" : "GRAPHQL_PRIMARY_RESULT_MISSING", result.Errors[0].Code);
            if (withErrors) Assert.Equal("field", (string?)Fixtures.Node(result.Errors[0])["details"]!["path"]![0]);
        }
    }

    [Fact]
    public async Task MetadataIdentityStopsDownloadBeforeLinksAndFilesystemChanges()
    {
        using var directory = new TemporaryDirectory();
        var calls = 0;
        using var client = Fixtures.Client(request =>
        {
            calls++;
            Assert.EndsWith("/files/2.json", request.RequestUri!.AbsolutePath);
            return Fixtures.Json(new { file_id = 3 });
        });
        var error = await Fixtures.Error("FILE_ID_MISMATCH", "nexus-v1",
            () => Fixtures.Commands(client, () => "fake-key").Execute(new DownloadCommand(Fixtures.Skyrim.Domain, "1", "2", directory.Path)));
        Assert.Equal("3", error.Details!["returned_file_id"]);
        Assert.Equal(1, calls);
        Assert.Empty(Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task MalformedRequiredFieldsHaveTheirApiSource()
    {
        foreach (var source in new[] { "nexus-v1", "nexus-v2", "nexus-v3" })
        {
            using var client = Fixtures.Client(_ => Fixtures.Json(source == "nexus-v2"
                ? new { data = new { mod = new { }, modFiles = Array.Empty<object>() } } : (object)new { data = new { } }));
            var http = new ApiHttp(client);
            await Fixtures.Error("UPSTREAM_SCHEMA_ERROR", source, () => source switch
            {
                "nexus-v1" => new NexusV1(http).File("fake-key", Fixtures.Skyrim.Domain, "1", "2"),
                "nexus-v2" => new NexusV2(http).Inspect(Fixtures.Skyrim, "1", false, false),
                _ => (Task)new NexusV3(http).Inspect(Fixtures.Skyrim.Domain, "2", "fake-key")
            });
        }
    }
}
