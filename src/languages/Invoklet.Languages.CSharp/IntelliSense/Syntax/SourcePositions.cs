using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>Whether a position of the analysis document is somewhere nothing can be completed or looked up: a comment or a literal.</summary>
internal static class SourcePositions
{
    // A position inside a // or /* */ comment (including /// doc comments) has nothing to complete —
    // model.LookupSymbols(pos)/GetSymbolInfo don't know about lexical trivia, so without this guard
    // they'd happily return whatever's in scope while the user is just typing prose.
    internal static bool IsInsideComment(SyntaxNode root, int position)
    {
        // TextSpan.Contains is half-open ([start, end)), so a position sitting exactly at the end of
        // a comment's span — which is exactly where the caret is while still typing an unterminated
        // "//..."/"/*..." at the point nothing follows it yet — doesn't count as "inside" by the
        // first check. Falling back to position - 1 catches that (by far the most common) case.
        return IsCommentTrivia(root.FindTrivia(position))
            || (position > 0 && IsCommentTrivia(root.FindTrivia(position - 1)));
    }

    private static bool IsCommentTrivia(SyntaxTrivia trivia) =>
        trivia.IsKind(SyntaxKind.SingleLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineCommentTrivia)
            || trivia.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia)
            || trivia.IsKind(SyntaxKind.DocumentationCommentExteriorTrivia);

    // A position inside the text of a string/char literal has nothing to complete either — unlike
    // comments this isn't trivia, it's an ordinary token (StringLiteralToken etc.), so it needs its
    // own check rather than falling out of IsInsideComment. Interpolation holes
    // (the `{expr}` parts of `$"..."`) are deliberately NOT covered — their contents are ordinary
    // expression tokens (identifiers, dots, ...), not one of the kinds checked here, so completion
    // still works inside them, same as VS/Rider.
    internal static bool IsInsideStringOrCharLiteral(SyntaxNode root, int position)
    {
        // Same half-open-span boundary concern as IsInsideComment: the caret sitting at the very end
        // of an in-progress, unterminated literal (by far the most common case while typing) is at
        // the token's end, so also check position - 1.
        return IsStringOrCharLiteralToken(root.FindToken(position))
            || (position > 0 && IsStringOrCharLiteralToken(root.FindToken(position - 1)));
    }

    private static bool IsStringOrCharLiteralToken(SyntaxToken token) =>
        token.IsKind(SyntaxKind.StringLiteralToken)
            || token.IsKind(SyntaxKind.CharacterLiteralToken)
            || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
            || token.IsKind(SyntaxKind.Utf8StringLiteralToken)
            || token.IsKind(SyntaxKind.SingleLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.MultiLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.Utf8SingleLineRawStringLiteralToken)
            || token.IsKind(SyntaxKind.Utf8MultiLineRawStringLiteralToken);

    internal static bool IsDeclarationNamePosition(SyntaxNode root, int position)
    {
        return IsDeclarationIdentifierToken(root.FindToken(position))
            || (position > 0 && IsDeclarationIdentifierToken(root.FindToken(position - 1)));
    }

    // Every syntax kind below carries its own declared name as a plain Identifier token (not a
    // qualified/dotted NameSyntax, unlike e.g. namespace declarations — deliberately not covered here,
    // out of scope for the reported symptom) — checking `== token` (not just "is this kind of node")
    // rules out completion inside some OTHER part of the same declaration (a method's return type, a
    // parameter's type, an attribute, ...), where the caret is legitimately in expression/type position.
    internal static bool IsDeclarationIdentifierToken(SyntaxToken token) => token.Parent switch
    {
        MethodDeclarationSyntax n => n.Identifier == token,
        LocalFunctionStatementSyntax n => n.Identifier == token,
        ClassDeclarationSyntax n => n.Identifier == token,
        StructDeclarationSyntax n => n.Identifier == token,
        InterfaceDeclarationSyntax n => n.Identifier == token,
        RecordDeclarationSyntax n => n.Identifier == token,
        EnumDeclarationSyntax n => n.Identifier == token,
        EnumMemberDeclarationSyntax n => n.Identifier == token,
        PropertyDeclarationSyntax n => n.Identifier == token,
        VariableDeclaratorSyntax n => n.Identifier == token,
        ParameterSyntax n => n.Identifier == token,
        TypeParameterSyntax n => n.Identifier == token,
        _ => false,
    };

    // See _queryContinuationKeywords — true when the caret sits anywhere inside a LINQ query
    // expression (from ... to the final select/group-by clause) or a query continuation
    // (`into` ... onward), the only place from/join/into/let/orderby/select/group/on/equals/
    // ascending/descending/by mean anything.
    internal static bool IsInsideQueryExpression(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is QueryExpressionSyntax or QueryContinuationSyntax);

    // See _accessorKeywords — true when the caret sits inside a property/indexer/event's accessor
    // list (covers the empty "{ }" case, where the closing brace token itself belongs to the
    // AccessorListSyntax) or inside an already-parsed accessor's own body.
    internal static bool IsInsideAccessorContext(SyntaxNode root, int position) =>
        HasAncestorMatching(root, position, n => n is AccessorListSyntax or AccessorDeclarationSyntax);

    // Same half-open-span boundary concern as IsInsideComment/IsInsideStringOrCharLiteral above: the
    // caret while still typing sits at a token's end (exclusive), so also check position - 1.
    internal static bool HasAncestorMatching(SyntaxNode root, int position, Func<SyntaxNode, bool> predicate)
    {
        return Check(position) || (position > 0 && Check(position - 1));

        bool Check(int pos) => root.FindToken(pos).Parent?.AncestorsAndSelf().Any(predicate) == true;
    }

    internal static int CountArgumentsBefore(ArgumentListSyntax argList, int position)
    {
        var count = 0;
        foreach (var comma in argList.Arguments.GetSeparators())
        {
            if (comma.SpanStart < position) count++;
            else break;
        }
        return count;
    }

    // No argument has been parsed at argIndex at all (right after `(`, or right after a
    // trailing `,` with nothing typed since — Roslyn's error recovery doesn't produce an Arguments
    // entry for either), or one has but position sits at or before its own start (caret placed back
    // at the very start of an already-typed argument, e.g. via Home/click) — either way, nothing has
    // been typed for this slot yet.
    internal static bool IsArgumentSlotEmpty(ArgumentListSyntax argList, int argIndex, int position)
        => argIndex >= argList.Arguments.Count || position <= argList.Arguments[argIndex].SpanStart;

    internal static bool IsInsideArgumentList(ArgumentListSyntax list, int pos) =>
        pos >= list.OpenParenToken.Span.End && (list.CloseParenToken.IsMissing || pos <= list.CloseParenToken.SpanStart);

    // The methods or constructors a call can mean. Roslyn fills CandidateSymbols only when binding is
    // ambiguous, so a valid call would show just the one overload it resolved to: the whole member or
    // constructor group is read instead, and only a call with no group (through a delegate) keeps what
    // binding found. Shared by signature help and the expected-type completion.
    internal static List<IMethodSymbol> GetCandidateMethods(SemanticModel model, SyntaxNode callLike)
    {
        var symbolInfo = model.GetSymbolInfo(callLike);
        var methods = (symbolInfo.Symbol is IMethodSymbol s ? [s] : Array.Empty<IMethodSymbol>())
            .Concat(symbolInfo.CandidateSymbols.OfType<IMethodSymbol>())
            .ToList();

        if (callLike is InvocationExpressionSyntax { Expression: { } target })
        {
            var group = model.GetMemberGroup(target).OfType<IMethodSymbol>().ToList();
            if (group.Count > 0) methods = group;
        }
        else if (callLike is BaseObjectCreationExpressionSyntax objectCreation)
        {
            // GetTypeInfo of the creation expression itself: its Type child has no type for a class
            // declared in the document being analysed.
            if (model.GetTypeInfo(objectCreation).Type is INamedTypeSymbol namedType)
            {
                var constructors = namedType.Constructors.Where(c => !c.IsImplicitlyDeclared).ToList();
                if (constructors.Count > 0) methods = constructors;
            }
        }
        return methods;
    }
}
