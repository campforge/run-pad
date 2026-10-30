using Invoklet.Format.MdRun;
using Invoklet.Services;

namespace Invoklet.NonUI.Tests;

// The SUT has no injected dependencies: the fixture owns the test data (documents and paths) and creates a new
// instance for each test.
internal sealed class ScriptDocumentWriterFixture
{
    /// <summary>A directory path that is absolute on every system, so references can be rebased between two of them.</summary>
    public static string Folder(string name) => Path.Combine(Path.GetTempPath(), "invoklet-writer-tests", name);

    public static string ScriptIn(string folder, string name = "a.inv") => Path.Combine(Folder(folder), name);

    public MdRunDocument CreateDocument(Guid? id = null, List<string>? references = null)
    {
        var document = MdRunParser.TryParse(MdRunWriter.NewFile("Console.WriteLine(1);\n", title: "Test"))
            ?? throw new InvalidOperationException("A new file must parse.");
        document.FrontMatter.Id = id;
        document.FrontMatter.References = references;
        return document;
    }

    internal ScriptDocumentWriter CreateSut() => new();
}
