using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>
/// Where in C# source a type member could be written. Syntax only — the script is parsed on its
/// own, without the generated code around it, because a class, struct, record or interface body
/// reads the same either way.
/// </summary>
internal static class CSharpMemberPosition
{
    /// <summary>True when <paramref name="position"/> is inside the braces of a class, struct, record
    /// or interface that has no members yet, and not inside a comment or disabled code. A body that is
    /// still missing its closing brace counts too: that is how one looks right after typing
    /// <c>{</c> and Enter.</summary>
    public static bool IsInEmptyTypeBody(string code, int position, CancellationToken cancellationToken = default)
    {
        if (position < 0 || position > code.Length) return false;
        var root = CSharpSyntaxTree.ParseText(code, cancellationToken: cancellationToken).GetRoot(cancellationToken);

        var trivia = root.FindTrivia(position);
        if (trivia.Span.Start < position && position < trivia.Span.End
            && !trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia))
            return false;

        // The token the position follows, or the one it is inside.
        var left = root.FindToken(position);
        if (position <= left.SpanStart) left = left.GetPreviousToken();
        for (var node = left.Parent; node is not null; node = node.Parent)
        {
            if (node is TypeDeclarationSyntax type
                && !type.OpenBraceToken.IsMissing && position >= type.OpenBraceToken.Span.End
                && (type.CloseBraceToken.IsMissing || position <= type.CloseBraceToken.SpanStart))
                return type.Members.Count == 0;
            // Inside something that isn't a type body — a method, an initializer, a string…
            if (node.Span.Start < position && position < node.Span.End) return false;
        }
        return false;
    }
}
