using Invoklet.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Invoklet.Languages.CSharp.IntelliSense.CompletionItems;
using static Invoklet.Languages.CSharp.IntelliSense.CompletionKeywords;
using static Invoklet.Languages.CSharp.IntelliSense.ExpectedTypeCompletion;
using static Invoklet.Languages.CSharp.IntelliSense.ImportableTypes;
using static Invoklet.Languages.CSharp.IntelliSense.ObjectInitializerCompletions;
using static Invoklet.Languages.CSharp.IntelliSense.OverrideStubs;

namespace Invoklet.Languages.CSharp.IntelliSense;

// Comments, string literals and the name of a declaration being typed take no completion.
internal sealed class NoCompletionProvider : ICompletionProvider
{
    public SimpleCompletionList? Provide(CompletionRequest request)
    {
        var root = request.Root;
        var pos = request.Offset;
        return SourcePositions.IsInsideComment(root, pos) || SourcePositions.IsInsideStringOrCharLiteral(root, pos) || SourcePositions.IsDeclarationNamePosition(root, pos)
            ? new SimpleCompletionList([], CompletionContextKind.None)
            : null;
    }
}
