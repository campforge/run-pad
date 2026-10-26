using Xunit;

namespace RunPad.Format.RPad.Tests;

// The document's stable identity, the front matter's id: key.
public class RpadDocumentIdTests
{
    private const string Legacy = "---\nversion: \"1.0\"\nlanguage: csharp\ntargetSdk: latest\nkind: Statements\n---\n\n# Old\n\n```csharp run\n1.Dump();\n```\n";

    [Fact]
    public void NewFile_Should_HaveAnId()
    {
        var parsed = RpadParser.TryParse(RpadWriter.NewFile("1.Dump();\n"));

        Assert.NotNull(parsed!.FrontMatter.Id);
        Assert.NotEqual(Guid.Empty, parsed.FrontMatter.Id);
    }

    [Fact]
    public void NewFiles_Should_HaveDifferentIds()
    {
        var first = RpadParser.TryParse(RpadWriter.NewFile("1.Dump();\n"))!.FrontMatter.Id;
        var second = RpadParser.TryParse(RpadWriter.NewFile("1.Dump();\n"))!.FrontMatter.Id;

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Id_Should_SurviveWriteAndParse()
    {
        var id = Guid.NewGuid();
        var doc = RpadParser.TryParse(Legacy)!;
        doc.FrontMatter.Id = id;

        var written = RpadWriter.Write(doc);
        var reparsed = RpadParser.TryParse(written)!;

        Assert.Equal(id, reparsed.FrontMatter.Id);
        Assert.Contains($"\nid: {id:D}\n", written);
        Assert.Equal(written, RpadWriter.Write(reparsed));
    }

    // id: is a known key: it must not come back a second time as an unknown entry.
    [Fact]
    public void Id_Should_BeWrittenOnce()
    {
        var text = RpadWriter.NewFile("1.Dump();\n");

        var written = RpadWriter.Write(RpadParser.TryParse(text)!);

        Assert.Empty(RpadParser.TryParse(text)!.FrontMatter.UnknownEntries);
        Assert.Single(written.Split('\n'), l => l.StartsWith("id:", StringComparison.Ordinal));
    }

    // A file from before IDs reads without one, and the format alone doesn't invent one: the editor
    // gives it one when it saves the file.
    [Fact]
    public void LegacyFile_Should_ParseAndWriteWithoutAnId()
    {
        var doc = RpadParser.TryParse(Legacy)!;

        Assert.Null(doc.FrontMatter.Id);
        Assert.DoesNotContain("\nid:", RpadWriter.Write(doc));
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("\"\"")]
    public void InvalidId_Should_ReadAsNoId(string value)
    {
        var text = Legacy.Replace("version: \"1.0\"\n", $"version: \"1.0\"\nid: {value}\n");

        var doc = RpadParser.TryParse(text);

        Assert.NotNull(doc);
        Assert.Null(doc!.FrontMatter.Id);
    }

    [Fact]
    public void Snapshot_Should_KeepTheId()
    {
        var doc = RpadParser.TryParse(RpadWriter.NewFile("1.Dump();\n"))!;

        var restored = RpadDocument.FromSnapshot(doc.CaptureSnapshot());

        Assert.Equal(doc.FrontMatter.Id, restored.FrontMatter.Id);
    }
}
