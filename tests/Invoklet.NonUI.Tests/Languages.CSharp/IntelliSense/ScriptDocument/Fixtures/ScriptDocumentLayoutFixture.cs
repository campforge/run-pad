using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.ScriptDocument.Fixtures;

// ScriptDocumentLayout has no injected dependencies and holds no workspace; it parses only for the program kind, in memory.
internal sealed class ScriptDocumentLayoutFixture
{
    public const string ProgramCode = "void Main()\n{\n    Console.WriteLine(1);\n}\n";

    public ScriptDocumentLayout CreateSut() => new();
}
