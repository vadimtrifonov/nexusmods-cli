using System.Text.Json;

namespace NexusMods;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        using var apiClient = ApiHttp.CreateClient();
        using var cdnClient = Cdn.CreateClient();
        var http = new ApiHttp(apiClient);
        var commands = new Commands(
            new NexusV1(http),
            new NexusV2(http),
            new NexusV3(http),
            new Download(new Cdn(cdnClient)));
        return await Run(args, Console.Out, commands);
    }

    internal static async Task<int> Run(string[] args, TextWriter stdout, Commands commands)
    {
        async Task<int> Write(CommandResult result)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(result, App.Json));
            return result.Status == "failed" ? 1 : 0;
        }
        try
        {
            var parsed = CommandLine.Parse(args, async command => await Write(await commands.Execute(command)));
            return await parsed.InvokeAsync(new()
            {
                Output = stdout,
                Error = TextWriter.Null,
                EnableDefaultExceptionHandler = false,
                // Retain normal process termination; commands do not consume the parser's cancellation token.
                ProcessTerminationTimeout = null
            });
        }
        catch (Exception exception)
        {
            var error = CliException.Public(exception);
            return await Write(CommandResult.Failed([error], error.Source is null ? [] : [error.Source]));
        }
    }
}
