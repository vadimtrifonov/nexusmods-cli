using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

namespace NexusMods.Tests;

internal sealed class TemporaryDirectory : IDisposable
{
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexusmods-test-" + Guid.NewGuid().ToString("N"));
    internal TemporaryDirectory() => Directory.CreateDirectory(Path);
    internal string File(string name) => System.IO.Path.Combine(Path, name);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

internal sealed class StubHttp(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
}

// Non-seekable chunks reproduce interrupted and overlong HTTP bodies without buffering the response.
internal sealed class ChunkStream(byte[][] chunks, Exception? failure = null, bool stall = false) : Stream
{
    private int index;
    internal bool Disposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (index < chunks.Length)
        {
            var chunk = chunks[index++];
            chunk.CopyTo(buffer);
            return chunk.Length;
        }
        if (stall) await Task.Delay(Timeout.Infinite, cancellationToken);
        if (failure is not null) throw failure;
        return 0;
    }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
}

internal static class Fixtures
{
    internal static readonly NexusGame Skyrim = new("1704", "skyrimspecialedition");
    internal static HttpResponseMessage Game(NexusGame game) => Json(new { data = new { game = new { id = game.Id, domainName = game.Domain } } });
    internal static JsonNode Load(string name) => JsonNode.Parse(File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Fixtures", name + ".json")))!;
    internal static JsonNode Node(object value) => JsonSerializer.SerializeToNode(value, App.Json)!;
    internal static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value is JsonNode node ? node.ToJsonString() : JsonSerializer.Serialize(value), System.Text.Encoding.UTF8, "application/json")
    };
    internal static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> send) => new(new StubHttp(request => Task.FromResult(send(request))))
        { Timeout = Timeout.InfiniteTimeSpan };
    internal static Commands Commands(HttpClient client, Func<string>? key = null)
    {
        var http = new ApiHttp(client);
        return new Commands(
            new NexusV1(http),
            new NexusV2(http),
            new NexusV3(http),
            new Download(new Cdn(client)),
            key ?? (() => throw new InvalidOperationException("Unexpected credential access")));
    }
    internal static async Task<CommandResult> Run(Commands commands, params string[] args)
    {
        var output = new StringWriter();
        var exit = await Program.Run(args, output, commands);
        var result = JsonSerializer.Deserialize<CommandResult>(output.ToString(), App.Json)!;
        Assert.Equal(result.Status == "failed" ? 1 : 0, exit);
        return result;
    }
    internal static void Golden(string name, object value)
    {
        var actual = Node(value);
        (actual["meta"] as JsonObject)?.Remove("retrieved_at");
        (actual["data"]?["local"] as JsonObject)?.Remove("path");
        var expected = Load(name + ".expected");
        Assert.True(JsonNode.DeepEquals(expected, actual), $"Expected:\n{expected}\nActual:\n{actual}");
    }
    internal static async Task<PublicError> Error(string code, string source, Func<Task> action)
    {
        var exception = await Assert.ThrowsAsync<CliException>(action);
        Assert.Equal(code, exception.Error.Code);
        Assert.Equal(source, exception.Error.Source);
        return exception.Error;
    }
}
