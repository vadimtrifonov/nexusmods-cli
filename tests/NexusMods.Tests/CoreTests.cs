using System.Text;
using System.Text.Json;
using Xunit;

namespace NexusMods.Tests;

public sealed class CoreTests
{
    [Fact]
    public void MarkupPreservesEvidenceLinksLiteralConfigurationAndInvalidEntities()
    {
        Assert.Equal("Main\nFile", Markup.Text("[b]Main[/b]<br />File"));
        Assert.Equal("Docs (https://example.test/?a=1&b=2)", Markup.Text("<a href='https://example.test/?a=1&amp;b=2'><b>Docs</b></a>"));
        Assert.Equal("[Display]\n[VR only]\n<version> <code-file>", Markup.Text("[Display]\n[VR only]\n<version> <code-file>"));
        Assert.Equal("[image: https://example.test/image.png]", Markup.Text("[img]https://example.test/image.png[/img]"));
        Assert.Equal("😀 A &#1114112; &#xD800;", Markup.Text("&#x1F600; &#65; &#1114112; &#xD800;"));
        Assert.Equal("https://example.test/file", Markup.Text("[url=https://example.test/file][/url]"));
        Assert.Equal("© é &bogus; &AMP; &NBSP;", Markup.Text("&copy; &eacute; &bogus; &AMP; &NBSP;"));
        Assert.Equal("a b\nc", Markup.Text("a&nbsp; \u00A0b&nbsp;\n&nbsp;c"));
        Assert.Equal("&lt;b&gt;", Markup.Text("&amp;lt;b&amp;gt;"));
        Assert.Equal("<b>literal</b>", Markup.Text("&lt;b&gt;literal&lt;/b&gt;"));
        Assert.Equal("&#xFFFFFFFFFFFFFFFF;", Markup.Text("&#xFFFFFFFFFFFFFFFF;"));
        Assert.Equal("Docs (https://example.test/)", Markup.Text("<A class='link' HREF=\"https://example.test/\">Docs</A>"));
        Assert.Equal("Docs (https://example.test/)", Markup.Text("<a href=https://example.test/>Docs</a>"));
        Assert.Equal("Label", Markup.Text("<a>Label</a>"));
        Assert.Equal("<preference> <spanish> <b-version>", Markup.Text("<preference> <spanish> <b-version>"));
    }

    [Fact]
    public void RepeatedUnclosedMarkupDoesNotTriggerBacktrackingTimeouts()
    {
        foreach (var opening in new[] { "[url=x]", "[url]", "[img]", "[youtube]", "<a" })
        {
            var input = string.Concat(Enumerable.Repeat(opening, 10000));
            Assert.Equal(input, Markup.Text(input));
        }
        Assert.Equal("", Markup.Text(string.Concat(Enumerable.Repeat("<a href=x>", 10000))));
        Assert.Equal("value", Markup.Text(new string(' ', 70000) + "value"));
    }

    [Fact]
    public void JsonIdsPreservePrecisionAndRejectMalformedRequiredValues()
    {
        using var doc = JsonDocument.Parse("{\"id\":9007199254740993,\"opaque\":\"[1, 1704]\",\"bad\":null}");
        var root = new JsonValue(doc.RootElement, "fixture");
        Assert.Equal("9007199254740993", root["id"].Id());
        Assert.Equal("[1, 1704]", root["opaque"].OpaqueId());
        Assert.Throws<CliException>(() => root["bad"].Boolean());
        Assert.Throws<CliException>(() => root["missing"].Text());
    }

    [Fact]
    public void CredentialManagerRoundTripsAnIsolatedCredential()
    {
        var target = "nexusmods:test:" + Guid.NewGuid().ToString("N");
        try
        {
            Assert.False(Credentials.Exists(target));
            Assert.Equal("AUTH_NOT_CONFIGURED", Assert.Throws<CliException>(() => Credentials.Load(target)).Error.Code);
            Credentials.Store("fake-key-α-" + target, target);
            Assert.True(Credentials.Exists(target));
            Assert.Equal("fake-key-α-" + target, Credentials.Load(target));
            Assert.True(Credentials.Remove(target));
            Assert.False(Credentials.Remove(target));
        }
        finally { Credentials.Remove(target); }
    }

    [Fact]
    public async Task ArchiveHashReportsObservedBytesAndMd5()
    {
        using var directory = new TemporaryDirectory();
        var file = directory.File("fixture.zip");
        await File.WriteAllTextAsync(file, "fixture bytes");
        var hash = await Archive.Hash(file);
        Assert.Equal("13", hash.SizeBytes);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes("fixture bytes"))), hash.Md5);
        Assert.Equal(file, hash.Path);
    }

    [Fact]
    public async Task ArchiveHashDetectsSameSizedConcurrentMutation()
    {
        using var directory = new TemporaryDirectory();
        var file = directory.File("fixture.zip");
        await File.WriteAllBytesAsync(file, new byte[16 * 1024 * 1024]);
        using var writer = new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var hash = Archive.Hash(file);
        Assert.False(hash.IsCompleted);
        var modified = DateTime.UtcNow.AddHours(1);
        var changed = 0;
        while (!hash.IsCompleted)
        {
            writer.Position = 0;
            writer.WriteByte((byte)changed++);
            writer.Flush();
            File.SetLastWriteTimeUtc(writer.SafeFileHandle, modified.AddSeconds(changed));
            await Task.Yield();
        }
        Assert.True(changed > 0);
        await Fixtures.Error("ARCHIVE_CHANGED", "local-file", () => hash);
    }
}
