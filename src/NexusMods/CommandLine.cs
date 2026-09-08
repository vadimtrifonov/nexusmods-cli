using System.CommandLine;
using System.CommandLine.Help;
using System.CommandLine.Invocation;
using System.CommandLine.Parsing;
using System.Globalization;
using System.Text.RegularExpressions;
using CliCommand = System.CommandLine.Command;

namespace NexusMods;

internal abstract record Command;
internal sealed record SearchCommand(SearchOptions Options) : Command;
internal sealed record InspectCommand(InspectOptions Options) : Command;
internal sealed record IdentifyCommand(string Path) : Command;
internal sealed record DownloadCommand(string GameDomain, string ModId, string FileId, string OutputDirectory) : Command;
internal sealed record AuthCommand(string Action) : Command;
internal sealed record InspectOptions(string GameDomain, string ModId, bool Description, string? FileCategory, int FileOffset, int FileLimit,
    string? FileId, bool Changelog, bool Contents, string? ContentPath, string? ContentExtension, int ContentOffset, int ContentLimit);

internal static class CommandLine
{
    internal static ParseResult Parse(string[] args, Func<Command, Task<int>> execute)
    {
        var root = new RootCommand("Search Nexus Mods, inspect requirements, identify archives, and download selected files.");
        // No implicit response-file reads or auxiliary completion commands.
        root.Directives.Clear();
        var configuration = new ParserConfiguration { ResponseFileTokenReplacer = null };
        root.SetAction(parse => new HelpAction().Invoke(parse));
        var version = root.Options.OfType<VersionOption>().Single();
        version.Aliases.Add("-V");
        version.Action = new VersionAction();
        var help = new CliCommand("help", "Show root help.") { Hidden = true };
        help.SetAction(parse => root.Parse([], configuration).Invoke(parse.InvocationConfiguration));
        root.Add(help);
        root.Add(Search(execute));
        root.Add(Inspect(execute));
        root.Add(Identify(execute));
        root.Add(Download(execute));
        var auth = new CliCommand("auth", "Manage the Windows Credential Manager API key.");
        foreach (var (name, description) in new[]
        {
            ("set", "Prompt without echo, validate, and store the API key."),
            ("status", "Report credential configuration and account status."),
            ("remove", "Remove the stored API key.")
        })
        {
            var command = new CliCommand(name, description);
            command.SetAction(parse => execute(new AuthCommand(name)));
            auth.Add(command);
        }
        root.Add(auth);
        RejectDuplicateOptions(root);
        var result = root.Parse(args, configuration);
        if (result.Errors.Count > 0)
        {
            // Library messages can include arbitrary argument values; only declared names are safe to expose.
            var message = result.Errors[0].SymbolResult is OptionResult option
                ? option.IdentifierTokenCount > 1 ? $"Option specified more than once: {option.Option.Name}." : $"Invalid or missing value for {option.Option.Name}."
                : "Invalid command or arguments.";
            var name = result.CommandResult.Command == root ? "" : $" {result.CommandResult.Command.Name}";
            if (result.CommandResult.Parent is System.CommandLine.Parsing.CommandResult parent && parent.Command != root)
                name = $" {parent.Command.Name}{name}";
            throw Invalid($"{message} Run `nexusmods{name} --help` for usage.");
        }
        return result;
    }

    private static CliCommand Search(Func<Command, Task<int>> execute)
    {
        var game = Game();
        var query = Text("--query", "Search text.", required: true);
        var field = Text("--field", "Exact name or Nexus text matching.", required: true).AcceptOnlyFromAmong("name", "name-stemmed", "description");
        var sort = Text("--sort", "Default: name for exact names, relevance for text matches.").AcceptOnlyFromAmong("relevance", "endorsements", "downloads", "updated", "created", "name");
        var direction = Text("--direction", "Default: asc for name order, desc otherwise.").AcceptOnlyFromAmong("asc", "desc");
        var offset = Number("--offset", "Search offset.", 0, 0, 1_000_000);
        var limit = Number("--limit", "Maximum search results.", 20, 1, 100);
        var command = new CliCommand("search", "Search the selected game catalog.") { game, query, field, sort, direction, offset, limit };
        command.SetAction(parse =>
        {
            var text = parse.GetRequiredValue(query).Trim();
            if (text.Length == 0) throw Invalid("--query cannot be empty.");
            var match = parse.GetRequiredValue(field);
            var order = parse.GetValue(sort) ?? (match == "name" ? "name" : "relevance");
            return execute(new SearchCommand(new(GameDomain(parse.GetRequiredValue(game)), text, match, order, parse.GetValue(direction) ?? (order == "name" ? "asc" : "desc"),
                parse.GetValue(offset), parse.GetValue(limit))));
        });
        return command;
    }

    private static CliCommand Inspect(Func<Command, Task<int>> execute)
    {
        var game = Game();
        var mod = Text("--mod", "Positive mod ID or Nexus HTTPS URL in the selected game.", required: true);
        var description = new Option<bool>("--description") { Description = "Include the mod description.", Arity = ArgumentArity.Zero };
        var category = Text("--file-category", "Case-insensitive file-list category; cannot be combined with --file.");
        var fileOffset = Number("--file-offset", "File-list offset, newest uploads first; cannot be combined with --file.", 0, 0, 1_000_000);
        var fileLimit = Number("--file-limit", "Maximum listed files; cannot be combined with --file.", 50, 1, 500);
        var file = Text("--file", "Inspect a positive file ID instead of listing uploads; includes version dependencies.");
        var changelog = new Option<bool>("--changelog") { Description = "Include the selected file's changelog; requires --file.", Arity = ArgumentArity.Zero };
        var contents = new Option<bool>("--contents") { Description = "Query indexed archive contents; requires --file.", Arity = ArgumentArity.Zero };
        var path = Text("--content-path", "Literal substring of at least two characters, not a glob; requires --contents.");
        var extension = Text("--content-extension", "Extension with or without a leading dot; requires --contents.");
        var contentOffset = Number("--content-offset", "Content offset; requires --contents.", 0, 0, 1_000_000);
        var contentLimit = Number("--content-limit", "Maximum indexed entries; requires --contents.", 100, 1, 1_000);
        var command = new CliCommand("inspect", "Inspect a mod or selected file, including active author-declared requirements.")
            { game, mod, description, category, fileOffset, fileLimit, file, changelog, contents, path, extension, contentOffset, contentLimit };
        command.SetAction(parse =>
        {
            var id = parse.GetValue(file) is { } value ? PositiveId(value, "--file") : null;
            if (id is not null && new Option[] { category, fileOffset, fileLimit }.Any(option => parse.GetResult(option) is { Implicit: false }))
                throw Invalid("File-list options cannot be combined with --file.");
            var withChangelog = parse.GetValue(changelog);
            var withContents = parse.GetValue(contents);
            if ((withChangelog || withContents) && id is null) throw Invalid("--changelog and --contents require --file.");
            if (!withContents && new Option[] { path, extension, contentOffset, contentLimit }.Any(option => parse.GetResult(option) is { Implicit: false }))
                throw Invalid("Content filters require --contents.");
            var substring = parse.GetValue(path);
            if (substring is not null && (substring.Length < 2 || substring.IndexOfAny(['*', '?']) >= 0))
                throw Invalid("--content-path must be a substring of at least two characters, not a glob.");
            var suffix = parse.GetValue(extension);
            if (suffix is "" or ".") throw Invalid("--content-extension cannot be empty.");
            var domain = GameDomain(parse.GetRequiredValue(game));
            return execute(new InspectCommand(new(domain, ModId(parse.GetRequiredValue(mod), domain), parse.GetValue(description), parse.GetValue(category),
                parse.GetValue(fileOffset), parse.GetValue(fileLimit), id, withChangelog, withContents, substring, suffix,
                parse.GetValue(contentOffset), parse.GetValue(contentLimit))));
        });
        return command;
    }

    private static CliCommand Identify(Func<Command, Task<int>> execute)
    {
        var path = Text("--path", "Local archive path.", required: true);
        var command = new CliCommand("identify", "Hash a local archive and return every Nexus MD5 association.") { path };
        command.SetAction(parse => execute(new IdentifyCommand(parse.GetRequiredValue(path))));
        return command;
    }

    private static CliCommand Download(Func<Command, Task<int>> execute)
    {
        var game = Game();
        var mod = Text("--mod", "Positive mod ID.", required: true);
        var file = Text("--file", "Positive file ID.", required: true);
        var directory = Text("--output-dir", "Existing absolute output directory.", required: true);
        var command = new CliCommand("download", "Download the caller-selected file; requires Premium.") { game, mod, file, directory };
        command.SetAction(parse => execute(new DownloadCommand(GameDomain(parse.GetRequiredValue(game)), PositiveId(parse.GetRequiredValue(mod), "--mod"),
            PositiveId(parse.GetRequiredValue(file), "--file"), parse.GetRequiredValue(directory))));
        return command;
    }

    private static Option<string> Game() => Text("--game", "Nexus Mods game domain from the URL, not a numeric game ID.", required: true);

    private static Option<string> Text(string name, string description, bool required = false)
    {
        var option = new Option<string>(name) { Description = description, Required = required, Arity = ArgumentArity.ExactlyOne };
        if (required) option.Validators.Add(result =>
        {
            if (result.Tokens is [{ Value.Length: 0 }]) result.AddError($"{name} is required.");
        });
        return option;
    }

    private static Option<int> Number(string name, string description, int fallback, int min, int max)
    {
        var option = new Option<int>(name) { Description = $"{description} Range: {min}–{max}.", DefaultValueFactory = _ => fallback };
        option.CustomParser = result =>
        {
            if (result.Tokens.Count == 1 && int.TryParse(result.Tokens[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value >= min && value <= max)
                return value;
            result.AddError($"{name} must be between {min} and {max}.");
            return 0;
        };
        return option;
    }

    private static void RejectDuplicateOptions(CliCommand command)
    {
        foreach (var option in command.Options) option.Validators.Add(result =>
        {
            if (result.IdentifierTokenCount > 1) result.AddError($"Option specified more than once: {option.Name}.");
        });
        foreach (var child in command.Subcommands) RejectDuplicateOptions(child);
    }

    private sealed class VersionAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            parseResult.InvocationConfiguration.Output.WriteLine(App.Version);
            return 0;
        }
    }

    internal static string PositiveId(string value, string label)
    {
        if (!Regex.IsMatch(value, @"\A[1-9][0-9]*\z")) throw Invalid($"{label} must be a positive decimal ID.");
        return value;
    }
    private static string GameDomain(string value)
    {
        if (!Regex.IsMatch(value, @"\A[a-zA-Z0-9][a-zA-Z0-9_-]*\z"))
            throw Invalid("--game must be a Nexus Mods game domain, not a URL or path.");
        return value.ToLowerInvariant();
    }
    internal static string ModId(string value, string gameDomain)
    {
        if (Regex.IsMatch(value, @"\A[1-9][0-9]*\z")) return value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url)) throw Invalid("--mod must be a positive ID or Nexus mod URL.");
        if (url.Scheme != "https" || !(url.Host.Equals("nexusmods.com", StringComparison.OrdinalIgnoreCase) || url.Host.Equals("www.nexusmods.com", StringComparison.OrdinalIgnoreCase)))
            throw Invalid("--mod must be a Nexus Mods HTTPS URL.");
        var match = Regex.Match(url.AbsolutePath, $"^/{Regex.Escape(gameDomain)}/mods/([1-9][0-9]*)/?$", RegexOptions.IgnoreCase);
        if (!match.Success) throw Invalid("--mod URL must identify a mod in the selected game.");
        return match.Groups[1].Value;
    }
    private static CliException Invalid(string message) => new("INVALID_ARGUMENT", message);
}
