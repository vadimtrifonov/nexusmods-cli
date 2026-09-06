namespace NexusMods;

internal sealed class Commands(NexusV1 v1, NexusV2 v2, NexusV3 v3, Download download, Func<string>? loadKey = null)
{
    private readonly Func<string> key = loadKey ?? (() => Credentials.Load());
    private static CommandResult Missing(List<PublicError> errors, string message, string[] sources, Dictionary<string, string>? limits) =>
        CommandResult.Failed(errors.Count > 0 ? errors : [new("GRAPHQL_PRIMARY_RESULT_MISSING", message, "nexus-v2")], sources, limits);

    internal Task<CommandResult> Execute(Command command) => command switch
    {
        SearchCommand search => Search(search.Options),
        InspectCommand inspect => Inspect(inspect.Options),
        IdentifyCommand identify => Identify(identify.Path),
        DownloadCommand transfer => Download(transfer),
        AuthCommand auth => Auth(auth.Action),
        _ => throw new InvalidOperationException()
    };
    private async Task<CommandResult> Search(SearchOptions options)
    {
        var result = await v2.Search(options);
        return result.Data is null ? Missing(result.Errors, "Nexus did not return search results.", ["nexus-v2"], result.RateLimits)
            : CommandResult.Success(result.Data, ["nexus-v2"], result.Errors, result.RateLimits);
    }
    private async Task<CommandResult> Identify(string path)
    {
        var local = await Archive.Hash(path);
        if (local.SizeBytes == "0") throw new CliException("EMPTY_ARCHIVE", "The archive is empty.", "local-file");
        var result = await v2.Identify(local.Md5);
        return result.Data is null ? Missing(result.Errors, "Nexus did not return archive identification results.", ["local-file", "nexus-v2"], result.RateLimits)
            : CommandResult.Success(new { local, associations = result.Data }, ["local-file", "nexus-v2"], result.Errors, result.RateLimits);
    }
    private async Task<CommandResult> Download(DownloadCommand request)
    {
        var apiKey = key();
        var metadata = await v1.File(apiKey, request.GameDomain, request.ModId, request.FileId);
        var links = await v1.Links(apiKey, request.GameDomain, request.ModId, request.FileId);
        var file = metadata.File;
        var result = await download.Run(new(request.GameDomain, request.ModId, request.FileId, file.ArchiveName, request.OutputDirectory, file.SizeBytes, links.Urls));
        return CommandResult.Success(new
        {
            source = new { game_domain = request.GameDomain, mod_id = request.ModId, file_id = request.FileId, display_name = file.Name, version = file.Version, category = file.Category, metadata_size = file.SizeBytes },
            transfer = result.Receipt
        }, ["nexus-v1", "nexus-cdn", "local-file"], result.Errors, RateLimits.Merge(metadata.RateLimits, links.RateLimits));
    }
    private async Task<CommandResult> Auth(string action)
    {
        if (action == "remove") return CommandResult.Success(new { configured = false, removed = Credentials.Remove() }, [Credentials.Source]);
        if (action == "status" && !Credentials.Exists()) return CommandResult.Success(new { configured = false }, [Credentials.Source]);
        var apiKey = action == "set" ? Credentials.Prompt() : key();
        var account = await v1.ValidateKey(apiKey);
        if (action == "set") Credentials.Store(apiKey);
        return CommandResult.Success(new { configured = true, premium = account.Premium }, [Credentials.Source, "nexus-v1"], limits: account.RateLimits);
    }

    private async Task<CommandResult> Inspect(InspectOptions request)
    {
        var resolved = await v2.ResolveGame(request.GameDomain);
        if (resolved.Data is not { } game) return Missing(resolved.Errors, "Nexus did not return the requested game.", ["nexus-v2"], resolved.RateLimits);
        Evidence<ModBundle> result;
        try { result = await v2.Inspect(game, request.ModId, request.Description, request.Changelog); }
        catch (Exception exception)
        {
            return CommandResult.Failed([.. resolved.Errors, CliException.Public(exception, "nexus-v2")],
                ["nexus-v2"], resolved.RateLimits);
        }
        var errors = new List<PublicError>([.. resolved.Errors, .. result.Errors]);
        var limits = RateLimits.Merge(resolved.RateLimits, result.RateLimits);
        if (result.Data is not { } bundle) return Missing(errors, "Nexus did not return the requested mod.", ["nexus-v2"], limits);
        var sources = new List<string> { "nexus-v2" };
        var category = request.FileCategory?.ToUpperInvariant();
        var filtered = bundle.Files.Where(file => category is null || file.Category.Equals(category, StringComparison.OrdinalIgnoreCase)).ToArray();
        var files = filtered.Skip(request.FileOffset).Take(request.FileLimit).Select(file => file with { Changelog = null }).ToArray();
        var data = new Dictionary<string, object>
        {
            ["mod"] = bundle.Mod,
            ["files"] = new
            {
                total_count = filtered.Length, returned_count = files.Length, offset = request.FileOffset, limit = request.FileLimit, category,
                totals_by_category = bundle.Files.GroupBy(file => file.Category).ToDictionary(group => group.Key, group => group.Count()), records = files
            }
        };
        if (request.FileId is not null)
        {
            var selected = bundle.Files.FirstOrDefault(file => file.FileId == request.FileId)
                ?? throw new CliException("FILE_NOT_FOUND", "The selected file does not belong to the requested mod.", "nexus-v2");
            data["selected_file"] = selected;
            if (bundle.Mod.LegacyModRequirementsEnabled) data["dependencies"] = new { status = "not_applicable", reason = "legacy_dependency_model" };
            else
            {
                sources.Add("nexus-v3");
                try
                {
                    var dependencies = await v3.Inspect(game.Domain, selected.FileId, key());
                    var evidence = new Dictionary<string, object> { ["status"] = dependencies.Status, ["identity"] = dependencies.Identity };
                    if (dependencies.Raw is not null) evidence["raw"] = dependencies.Raw;
                    if (dependencies.Materialized is not null) evidence["materialized"] = dependencies.Materialized;
                    data["dependencies"] = evidence;
                    errors.AddRange(dependencies.Errors);
                    limits = RateLimits.Merge(limits, dependencies.RateLimits);
                }
                catch (Exception exception)
                {
                    errors.Add(CliException.Public(exception, "nexus-v3", "dependencies"));
                    data["dependencies"] = new { status = "error" };
                }
            }
            if (request.Contents)
            {
                try
                {
                    var contents = await v2.Contents(new(game.Id, request.ModId, selected.FileId, request.ContentOffset, request.ContentLimit, request.ContentPath, request.ContentExtension));
                    limits = RateLimits.Merge(limits, contents.RateLimits);
                    errors.AddRange(contents.Errors.Select(error => error with { Section = "contents" }));
                    if (contents.Data is not { } content)
                    {
                        if (contents.Errors.Count == 0) errors.Add(new("GRAPHQL_PRIMARY_RESULT_MISSING", "Nexus did not return indexed file contents.", "nexus-v2", "contents"));
                        data["contents"] = new { status = "error" };
                    }
                    else data["contents"] = new
                    {
                        status = contents.Errors.Count == 0 ? "ok" : "partial", content.TotalCount, content.ReturnedCount, content.Offset, content.Limit, content.Entries
                    };
                }
                catch (Exception exception)
                {
                    errors.Add(CliException.Public(exception, "nexus-v2", "contents"));
                    data["contents"] = new { status = "error" };
                }
            }
        }
        var requirements = await v2.Requirements(game.Id, request.ModId);
        errors.AddRange(requirements.Errors.Select(error => error with { Section = "author_declared_requirements" }));
        limits = RateLimits.Merge(limits, requirements.RateLimits);
        data["author_declared_requirements"] = requirements.Data is { } declared
            ? new { status = requirements.Errors.Count == 0 ? "ok" : "partial", declared.Nexus, declared.Dlc }
            : new { status = "error" };
        return CommandResult.Success(data, sources, errors, limits);
    }
}
