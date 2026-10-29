using Xunit;

namespace Invoklet.Format.RPad.Tests;

// Extracted from EditorTabsControl's private PeekLanguage so the main UI project's file
// tree can reuse the same cheap front-matter peek for its per-language icon, instead of duplicating
// the parse-and-default logic a second time. Lives in this project (not the main Invoklet one) because
// it's fundamentally a .rpad-format concern, next to RpadParser/RpadLanguage themselves.
public class RpadFilePeekTests
{
    [Fact]
    public void PeekLanguage_Should_ReturnCSharp_ForACSharpFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, RpadWriter.NewFile("Console.WriteLine(\"Hello, RPad!\");\n", "Demo", kind: RpadKind.Statements));
            Assert.Equal(RpadLanguage.CSharp, RpadFilePeek.PeekLanguage(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void PeekLanguage_Should_ReturnVb_ForAVbFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "---\nversion: \"1.0\"\nlanguage: vb\ntargetSdk: net10.0\nkind: Statements\n---\n\n# Demo\n\n```vb run\nConsole.WriteLine(\"hi\")\n```\n");
            Assert.Equal(RpadLanguage.Vb, RpadFilePeek.PeekLanguage(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void PeekLanguage_Should_DefaultToCSharp_WhenTheFileDoesNotExist()
        => Assert.Equal(RpadLanguage.CSharp, RpadFilePeek.PeekLanguage("/no/such/file.rpad"));

    [Fact]
    public void PeekLanguage_Should_DefaultToCSharp_ForANullPath()
        => Assert.Equal(RpadLanguage.CSharp, RpadFilePeek.PeekLanguage(null));

    // An invalid document has no trustworthy language — PeekDocumentLanguage says so
    // (null) so the tab gets no language services; PeekLanguage keeps its C# display default.
    [Fact]
    public void PeekDocumentLanguage_Should_ReturnNull_ForAnInvalidDocument()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, "---\nlanguage: vb\nkind: nonsense\n---\n\n```vb run\nConsole.WriteLine(1)\n```\n");
            Assert.Null(RpadFilePeek.PeekDocumentLanguage(file));
            Assert.Equal(RpadLanguage.CSharp, RpadFilePeek.PeekLanguage(file));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void PeekDocumentLanguage_Should_ReturnTheDeclaredLanguage_ForAValidDocument()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, RpadWriter.NewFile("Console.WriteLine(\"Hello, RPad!\")\n", "Demo", language: RpadLanguage.Vb));
            Assert.Equal(RpadLanguage.Vb, RpadFilePeek.PeekDocumentLanguage(file));
        }
        finally { File.Delete(file); }
    }
}
