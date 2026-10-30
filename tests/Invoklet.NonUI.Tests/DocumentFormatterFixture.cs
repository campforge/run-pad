using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invoklet.NonUI.Tests;

internal sealed class DocumentFormatterFixture : IDisposable
{
    public ScriptDocumentLayout Layout { get; } = new();

    public ScriptDocumentSession Session { get; }

    // The core library only, enough for int and bool to exist; the breakpoint conditions need the types of the language.
    public DocumentFormatterFixture() => Session = new ScriptDocumentSession(Layout, new List<MetadataReference>
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    });

    public DocumentFormatter CreateSut() => new(Session, Layout, NullLogger.Instance);

    public BreakpointConditionAnalyzer CreateAnalyzer() => new(Session.Sync, Layout);

    public SignatureHelp CreateSignatureHelp() => new(Session.Sync, Layout, NullLogger.Instance);

    public RefactoringService CreateRefactorings()
        => new(Session, Session.Sync, Layout, new RoslynCodeFixEngine(InvokletRoslynHost.CompositionHost, NullLogger.Instance), NullLogger.Instance);

    public void Dispose() => Session.Dispose();
}
