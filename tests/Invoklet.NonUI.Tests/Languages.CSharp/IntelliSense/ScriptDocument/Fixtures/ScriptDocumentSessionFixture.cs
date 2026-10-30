using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Microsoft.CodeAnalysis;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.ScriptDocument.Fixtures;

// The session is Roslyn's own workspace through and through, so it is not faked: it is made over no references (these tests look at
// the text and the bookkeeping of the document, not at what it means) and disposed by the test.
internal sealed class ScriptDocumentSessionFixture
{
    public ScriptDocumentLayout Layout { get; } = new();

    public ScriptDocumentSession CreateSut() => new(Layout, new List<MetadataReference>());
}
