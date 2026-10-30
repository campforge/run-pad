using Invoklet.Languages.CSharp.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.Logging.Abstractions;

namespace Invoklet.NonUI.Tests;

// The context changes the workspace of a session, which is Roslyn's own and is not faked; the session is made over no references.
internal sealed class ScriptContextFixture : IDisposable
{
    private readonly List<ScriptDocumentSession> _sessions = [];

    public ScriptDocumentSession Session { get; private set; } = null!;

    public ScriptContext CreateSut()
    {
        var layout = new ScriptDocumentLayout();
        Session = new ScriptDocumentSession(layout, new List<MetadataReference>());
        _sessions.Add(Session);
        return new ScriptContext(Session, layout, new ReferenceSet([], [], []), NullLogger.Instance);
    }

    public void Dispose()
    {
        foreach (var session in _sessions) session.Dispose();
    }
}
