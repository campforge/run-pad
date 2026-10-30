using System.Text;
using Invoklet.Format.MdRun;
using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class ScriptDocumentWriterTests
{
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];

    [Fact]
    public void Serialize_NoDocument_WritesTheTextAsItIsWithoutAByteOrderMark()
    {
        // Arrange
        var sut = new ScriptDocumentWriterFixture().CreateSut();
        var path = ScriptDocumentWriterFixture.ScriptIn("a");

        // Act
        var written = sut.Serialize(null, "not a document\r\nЖ", path, path, null);

        // Assert
        Assert.Equal(Encoding.UTF8.GetBytes("not a document\r\nЖ"), written.Bytes);
        Assert.Null(written.DocumentId);
    }

    [Fact]
    public void Serialize_Document_StartsWithTheByteOrderMarkAndCarriesTheTextAsItsCode()
    {
        // Arrange
        var fixture = new ScriptDocumentWriterFixture();
        var sut = fixture.CreateSut();
        var path = ScriptDocumentWriterFixture.ScriptIn("a");

        // Act
        var written = sut.Serialize(fixture.CreateDocument(), "var a = 42;\n", path, path, null);

        // Assert
        Assert.Equal(Bom, written.Bytes.Take(3));
        var parsed = MdRunParser.TryParse(Encoding.UTF8.GetString(written.Bytes, 3, written.Bytes.Length - 3));
        Assert.Equal("var a = 42;\n", parsed!.RunCode);
    }

    [Fact]
    public void Serialize_OwnFileOfADocumentWithoutAnId_GivesItOneAndKeepsIt()
    {
        // Arrange
        var fixture = new ScriptDocumentWriterFixture();
        var sut = fixture.CreateSut();
        var document = fixture.CreateDocument(id: null);
        var path = ScriptDocumentWriterFixture.ScriptIn("a");

        // Act
        var written = sut.Serialize(document, "text", path, path, null);

        // Assert
        Assert.NotNull(document.FrontMatter.Id);
        Assert.Equal(document.FrontMatter.Id, written.DocumentId);
    }

    [Fact]
    public void Serialize_OwnFileOfADocumentWithAnId_KeepsThatId()
    {
        // Arrange
        var fixture = new ScriptDocumentWriterFixture();
        var sut = fixture.CreateSut();
        var id = Guid.NewGuid();
        var document = fixture.CreateDocument(id);
        var path = ScriptDocumentWriterFixture.ScriptIn("a");

        // Act
        var written = sut.Serialize(document, "text", path, path, null);

        // Assert
        Assert.Equal(id, written.DocumentId);
        Assert.Equal(id, document.FrontMatter.Id);
    }

    [Fact]
    public void Serialize_AnotherFile_WritesANewIdAndLeavesTheDocumentsOwn()
    {
        // Arrange
        var fixture = new ScriptDocumentWriterFixture();
        var sut = fixture.CreateSut();
        var id = Guid.NewGuid();
        var document = fixture.CreateDocument(id);

        // Act
        var written = sut.Serialize(document, "text", ScriptDocumentWriterFixture.ScriptIn("b"), ScriptDocumentWriterFixture.ScriptIn("a"), null);

        // Assert
        Assert.NotNull(written.DocumentId);
        Assert.NotEqual(id, written.DocumentId);
        Assert.Equal(id, document.FrontMatter.Id); // the tab takes the new one only once it has moved
    }

    [Fact]
    public void Serialize_UntitledTab_WritesAnIdOfItsOwnAndLeavesTheDocumentWithoutOne()
    {
        // Arrange
        var fixture = new ScriptDocumentWriterFixture();
        var sut = fixture.CreateSut();
        var document = fixture.CreateDocument(id: null);

        // Act
        var written = sut.Serialize(document, "text", ScriptDocumentWriterFixture.ScriptIn("b"), null, null);

        // Assert
        Assert.NotNull(written.DocumentId);
        Assert.Null(document.FrontMatter.Id);
    }

    [Fact]
    public void Serialize_AnotherFolder_RewritesRelativeReferencesInTheBytesOnly()
    {
        // Arrange
        var fixture = new ScriptDocumentWriterFixture();
        var sut = fixture.CreateSut();
        List<string> references = ["lib/a.dll"];
        var document = fixture.CreateDocument(references: [.. references]);
        var from = ScriptDocumentWriterFixture.ScriptIn("old");
        var to = ScriptDocumentWriterFixture.ScriptIn("new");

        // Act
        var written = sut.Serialize(document, "text", to, from, from);

        // Assert
        var writtenReferences = MdRunParser.TryParse(Encoding.UTF8.GetString(written.Bytes, 3, written.Bytes.Length - 3))!.FrontMatter.References!;
        var stillTheSameFile = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(to)!, writtenReferences[0]))
            == Path.GetFullPath(Path.Combine(Path.GetDirectoryName(from)!, "lib/a.dll"));
        Assert.NotEqual(references, writtenReferences);
        Assert.True(stillTheSameFile);
        Assert.Equal(references, document.FrontMatter.References); // the document keeps its own until it moves
    }

    [Fact]
    public void Serialize_SameFolder_LeavesReferencesAsTheyAre()
    {
        // Arrange
        var fixture = new ScriptDocumentWriterFixture();
        var sut = fixture.CreateSut();
        var document = fixture.CreateDocument(references: ["lib/a.dll"]);
        var from = ScriptDocumentWriterFixture.ScriptIn("old", "a.inv");
        var to = ScriptDocumentWriterFixture.ScriptIn("old", "b.inv");

        // Act
        var written = sut.Serialize(document, "text", to, from, from);

        // Assert
        var writtenReferences = MdRunParser.TryParse(Encoding.UTF8.GetString(written.Bytes, 3, written.Bytes.Length - 3))!.FrontMatter.References;
        Assert.Equal(["lib/a.dll"], writtenReferences);
    }

    [Fact]
    public void RebasedReferences_NoFileToBeRelativeTo_ReturnsNull()
    {
        // Arrange
        var sut = new ScriptDocumentWriterFixture().CreateSut();

        // Act
        var rebased = sut.RebasedReferences(["lib/a.dll"], null, ScriptDocumentWriterFixture.ScriptIn("new"));

        // Assert
        Assert.Null(rebased);
    }
}
