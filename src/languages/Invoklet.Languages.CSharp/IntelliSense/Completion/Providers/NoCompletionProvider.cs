using Invoklet.Languages.CSharp.IntelliSense.Syntax;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion.Providers;

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
