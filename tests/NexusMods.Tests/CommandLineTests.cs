using System.Text.Json;
using Xunit;

namespace NexusMods.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public async Task GeneratedHelpAndVersionDoNotExecuteCommands()
    {
        using var client = Fixtures.Client(_ => throw new InvalidOperationException("No request expected"));
        foreach (var args in new string[][]
        {
            [], ["--help"], ["-h"], ["help"], ["search", "--help"], ["inspect", "--help"],
            ["identify", "--help"], ["download", "--help"], ["auth", "--help"],
            ["auth", "set", "--help"], ["auth", "status", "--help"], ["auth", "remove", "--help"]
        })
        {
            var output = new StringWriter();
            Assert.Equal(0, await Program.Run(args, output, Fixtures.Commands(client)));
            Assert.Contains("Usage:", output.ToString());
            Assert.Contains("--help", output.ToString());
            if (args is ["search" or "inspect" or "download", "--help"]) Assert.Contains("--game", output.ToString());
            if (args is ["identify" or "auth", "--help"]) Assert.DoesNotContain("--game", output.ToString());
            if (args is ["inspect", "--help"])
            {
                Assert.Contains("--mod", output.ToString());
                Assert.Contains("--content-limit", output.ToString());
                Assert.Contains("1000", output.ToString());
                Assert.DoesNotContain("--output-dir", output.ToString());
            }
        }
        foreach (var option in new[] { "--version", "-V" })
        {
            var output = new StringWriter();
            Assert.Equal(0, await Program.Run([option], output, Fixtures.Commands(client)));
            Assert.Equal(App.Version + Environment.NewLine, output.ToString());
        }
    }

    [Fact]
    public async Task TypedOptionsKeepDefaultsBoundsAndDomainBindings()
    {
        static async Task<Command> Bind(params string[] args)
        {
            Command? command = null;
            var parsed = CommandLine.Parse(args, value => { command = value; return Task.FromResult(0); });
            Assert.Equal(0, await parsed.InvokeAsync(new() { EnableDefaultExceptionHandler = false, ProcessTerminationTimeout = null }));
            return Assert.IsAssignableFrom<Command>(command);
        }

        const string game = "--game=skyrimspecialedition";
        foreach (var field in new[] { "name", "name-stemmed", "description" })
        {
            var search = Assert.IsType<SearchCommand>(await Bind("search", game, "--query= SkyUI ", "--field", field));
            Assert.Equal(new SearchOptions(Fixtures.Skyrim.Domain, "SkyUI", field, field == "name" ? "name" : "relevance", field == "name" ? "asc" : "desc", 0, 20), search.Options);
        }
        var ordered = Assert.IsType<SearchCommand>(await Bind("search", game, "--query", "q", "--field", "description", "--sort", "name", "--offset", "1000000", "--limit=100"));
        Assert.Equal(new SearchOptions(Fixtures.Skyrim.Domain, "q", "description", "name", "asc", 1_000_000, 100), ordered.Options);
        var reversed = Assert.IsType<SearchCommand>(await Bind("search", game, "--query", "q", "--field", "name", "--sort", "updated", "--direction", "asc"));
        Assert.Equal("updated", reversed.Options.Sort);
        Assert.Equal("asc", reversed.Options.Direction);
        var inspect = Assert.IsType<InspectCommand>(await Bind("inspect", game, "--mod", "https://www.nexusmods.com/skyrimspecialedition/mods/12604?tab=files"));
        Assert.Equal(new InspectOptions(Fixtures.Skyrim.Domain, "12604", false, null, 0, 50, null, false, false, null, null, 0, 100), inspect.Options);
        var page = Assert.IsType<InspectCommand>(await Bind("inspect", game, "--mod=1", "--file-category=MAIN", "--file-offset=1000000", "--file-limit=500"));
        Assert.Equal(new InspectOptions(Fixtures.Skyrim.Domain, "1", false, "MAIN", 1_000_000, 500, null, false, false, null, null, 0, 100), page.Options);
        var contents = Assert.IsType<InspectCommand>(await Bind("inspect", game, "--mod", "1", "--file=2", "--description", "--changelog", "--contents",
            "--content-path=meshes/", "--content-extension=.nif", "--content-offset=1000000", "--content-limit=1000"));
        Assert.Equal(new InspectOptions(Fixtures.Skyrim.Domain, "1", true, null, 0, 50, "2", true, true, "meshes/", ".nif", 1_000_000, 1000), contents.Options);
        foreach (var domain in new[] { "skyrimspecialedition", "fallout4" })
        {
            var selected = Assert.IsType<InspectCommand>(await Bind("inspect", "--game", domain.ToUpperInvariant(), "--mod", $"https://www.nexusmods.com/{domain}/mods/1"));
            Assert.Equal(domain, selected.Options.GameDomain);
            Assert.Equal("1", selected.Options.ModId);
            Assert.Equal(new DownloadCommand(domain, "1", "9007199254740993", "C:\\Downloads"), await Bind("download", "--game", domain, "--mod=1", "--file=9007199254740993", "--output-dir=C:\\Downloads"));
        }
        Assert.Equal(new IdentifyCommand("@literal.zip"), await Bind("identify", "--path", "@literal.zip"));
        foreach (var action in new[] { "set", "status", "remove" }) Assert.Equal(new AuthCommand(action), await Bind("auth", action));
    }

    [Fact]
    public async Task CatalogSelectionIsRequiredOnlyForScopedCommandsAndMustMatchModUrls()
    {
        var requests = 0;
        using var client = Fixtures.Client(_ => { requests++; throw new InvalidOperationException("No request expected"); });
        foreach (var args in new string[][]
        {
            ["search", "--query=q", "--field=name"],
            ["inspect", "--mod=1"],
            ["inspect", "--mod=https://www.nexusmods.com/skyrimspecialedition/mods/1"],
            ["download", "--mod=1", "--file=2", "--output-dir=C:\\Downloads"],
            ["inspect", "--game=", "--mod=1"],
            ["inspect", "--game", "--mod=1"],
            ["inspect", "--game=../fake-secret", "--mod=1"],
            ["inspect", "--game=https://example.test/fake-secret", "--mod=1"],
            ["inspect", "--game=fallout4\n", "--mod=1"],
            ["inspect", "--game=fallout4", "--game=fake-secret", "--mod=1"],
            ["inspect", "--game=fake-secret", "--mod=https://www.nexusmods.com/skyrimspecialedition/mods/1"],
            ["identify", "--game=fallout4", "--path=unused.zip"],
            ["auth", "status", "--game=fallout4"]
        })
        {
            var result = await Fixtures.Run(Fixtures.Commands(client), args);
            Assert.Equal("failed", result.Status);
            Assert.Equal("INVALID_ARGUMENT", Assert.Single(result.Errors).Code);
            Assert.DoesNotContain("fake-secret", Fixtures.Node(result).ToJsonString());
        }
        Assert.Equal(0, requests);
    }

    [Fact]
    public async Task UnexpectedInvocationFailuresKeepTheSanitizedJsonBoundary()
    {
        using var directory = new TemporaryDirectory();
        using var client = Fixtures.Client(_ => throw new InvalidOperationException("No request expected"));
        var result = await Fixtures.Run(Fixtures.Commands(client, () => throw new InvalidOperationException("fake-secret")),
            "download", "--game=skyrimspecialedition", "--mod=1", "--file=2", "--output-dir", directory.Path);
        Assert.Equal("failed", result.Status);
        Assert.Null(result.Data);
        Assert.Equal("INTERNAL_ERROR", Assert.Single(result.Errors).Code);
        Assert.DoesNotContain("fake-secret", Fixtures.Node(result).ToJsonString());
    }

    [Fact]
    public async Task InvalidArgumentsFailBeforeRequestsAndKeepSanitizedJsonOutput()
    {
        using var directory = new TemporaryDirectory();
        var responseFile = directory.File("arguments.txt");
        await File.WriteAllTextAsync(responseFile, "auth remove");
        var requests = 0;
        using var client = Fixtures.Client(_ => { requests++; throw new InvalidOperationException("No request expected"); });
        foreach (var args in new string[][]
        {
            ["fake-secret"], ["inspect"], ["inspect", "--mod"], ["inspect", "--mod="],
            ["inspect", "--mod", "https://www.nexusmods.com/skyrim/mods/1"],
            ["inspect", "--mod=1\n"],
            ["inspect", "--mod=1", "--file=2\n"],
            ["inspect", "--mod", "1", "--file", "2", "--contents", "--content-path", "*.nif"],
            ["inspect", "--mod", "1", "--file", "2", "--contents", "--content-path", "x"],
            ["inspect", "--mod", "1", "--content-extension", "nif"],
            ["inspect", "--mod", "1", "--changelog"],
            ["search", "--query", " ", "--field", "name"],
            ["search", "--query=q"], ["search", "--query=q", "--field=fake-secret"],
            ["search", "--query=q", "--field=name", "--limit=fake-secret"],
            ["search", "--query=q", "--field=name", "--limit=2147483648"],
            ["search", "--query=q", "--field=name", "--limit=0"],
            ["search", "--query=q", "--field=name", "--limit=101"],
            ["search", "--query=q", "--field=name", "--offset=-1"],
            ["search", "--query=q", "--field=name", "--offset=1000001"],
            ["inspect", "--mod=1", "--mod=fake-secret"],
            ["inspect", "--mod=1", "--description", "--description"],
            ["inspect", "--mod=1", "--description=fake-secret"],
            ["inspect", "--mod=1", "--description=false"],
            ["inspect", "--mod=1", "--file-limit=501"],
            ["inspect", "--mod=1", "--file=2", "--file-category=MAIN"],
            ["inspect", "--mod=1", "--file=2", "--file-offset=0"],
            ["inspect", "--mod=1", "--file=2", "--file-limit=50"],
            ["inspect", "--mod=1", "--file=2", "--contents", "--content-limit=1001"],
            ["inspect", "--mod=1", "--content-offset=0"],
            ["inspect", "--mod=1", "--content-limit=100"],
            ["identify", "--path="], ["download", "--mod=1", "--file=2", "--output-dir="],
            ["download", "--mod", "0", "--file", "2", "--output-dir", "C:\\Downloads"],
            ["download", "--mod=1\n", "--file=2", "--output-dir", directory.Path],
            ["download", "--mod=1", "--file=2\n", "--output-dir", directory.Path],
            ["auth"], ["auth", "fake-secret"], ["auth", "set", "--apikey", "fake-secret"], ["auth", "set", "--apikey=fake-secret"],
            ["auth", "status", "--fake-secret"], ["auth", "remove", "fake-secret"],
            ["[suggest]", "fake-secret"], ["@" + responseFile]
        })
        {
            string[] invocation = args[0] is "search" or "inspect" or "download"
                ? [args[0], "--game=skyrimspecialedition", .. args.Skip(1)] : args;
            var output = new StringWriter();
            Assert.Equal(1, await Program.Run(invocation, output, Fixtures.Commands(client)));
            using var document = JsonDocument.Parse(output.ToString());
            var result = document.RootElement;
            Assert.Equal("failed", result.GetProperty("status").GetString());
            Assert.False(result.TryGetProperty("data", out _));
            var code = Assert.Single(result.GetProperty("errors").EnumerateArray()).GetProperty("code").GetString();
            Assert.True(code == "INVALID_ARGUMENT", $"Expected INVALID_ARGUMENT for {string.Join(' ', args)}; got {code}.");
            Assert.DoesNotContain("fake-secret", output.ToString());
        }
        Assert.Equal(0, requests);
    }
}
