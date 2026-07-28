using Xunit;

namespace RunPad.Format.RPad.Tests;

// TODO.md #70: extracted from EditorTabsControl's private PeekLanguage so the main UI project's file
// tree can reuse the same cheap front-matter peek for its per-language icon, instead of duplicating
// the parse-and-default logic a second time. Lives in this project (not the main RunPad one) because
// it's fundamentally a .rpad-format concern, next to RpadParser/RpadLanguage themselves.
public class RpadFilePeekTests
{
    [Fact]
    public void PeekLanguage_Should_ReturnCSharp_ForACSharpFile()
    {
        var file = Path.GetTempFileName();
        try
        {
            File.WriteAllText(file, RpadWriter.NewFile("Demo", kind: RpadKind.Statements));
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
}
