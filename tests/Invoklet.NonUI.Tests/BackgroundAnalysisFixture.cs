using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invoklet.NonUI.Tests;

// The analysis runs on Roslyn's own workspace and engines; none of them is faked. The session has the core library only.
internal sealed class BackgroundAnalysisFixture : IDisposable
{
    private readonly ScriptDocumentLayout _layout = new();

    public ScriptDocumentSession Session { get; }

    public List<IReadOnlyList<IDiagnosticInfo>> Diagnostics { get; } = [];

    public List<IReadOnlyList<ISemanticToken>> Tokens { get; } = [];

    public BackgroundAnalysisFixture() => Session = new ScriptDocumentSession(_layout, new List<MetadataReference>
    {
        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
        MetadataReference.CreateFromFile(Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "System.Runtime.dll")),
    });

    public BackgroundAnalysis CreateSut()
    {
        var context = new ScriptContext(Session, _layout, new ReferenceSet([], [], []), NullLogger.Instance);
        var sut = new BackgroundAnalysis(Session, Session.Sync, _layout, context,
            new RoslynCodeFixEngine(InvokletRoslynHost.CompositionHost, NullLogger.Instance),
            new RoslynAnalyzerEngine(NullLogger.Instance), () => false);
        sut.DiagnosticsChanged += (_, list) => Diagnostics.Add(list);
        sut.SemanticTokensChanged += (_, list) => Tokens.Add(list);
        return sut;
    }

    public void Dispose() => Session.Dispose();
}
