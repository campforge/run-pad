using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Text;
using Invoklet.Contracts.Connections;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.Compiler;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Languages.DotnetCommon.Compiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Classification;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;
using NuGet.Configuration;
using static Invoklet.Languages.CSharp.IntelliSense.CompletionItems;
using static Invoklet.Languages.CSharp.IntelliSense.CompletionKeywords;
using static Invoklet.Languages.CSharp.IntelliSense.ExpectedTypeCompletion;
using static Invoklet.Languages.CSharp.IntelliSense.ImportableTypes;
using static Invoklet.Languages.CSharp.IntelliSense.ObjectInitializerCompletions;
using static Invoklet.Languages.CSharp.IntelliSense.OverrideStubs;
using ContractSeverity = Invoklet.Contracts.IntelliSense.DiagnosticSeverity;
using ExecutionRequest = Invoklet.Contracts.Models.ExecutionRequest;
using RoslynSeverity = Microsoft.CodeAnalysis.DiagnosticSeverity;

namespace Invoklet.Languages.CSharp.IntelliSense;

/// <summary>The keywords completion offers and where each is legal.</summary>
internal static class CompletionKeywords
{
    internal static readonly IReadOnlyList<ICompletionItem> _keywords =
        new[]
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
            "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
            "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
            "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
            "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
            "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
            "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
            "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
            "void", "volatile", "while", "async", "await", "var", "dynamic", "yield", "get", "set",
            "add", "remove", "partial", "where", "select", "from", "let", "orderby", "group", "join",
            "into", "ascending", "descending", "on", "equals", "by", "when", "init", "record",
            "with", "not", "and", "or", "nint", "nuint", "file", "scoped", "required", "global",
            "nameof", "value", "unmanaged", "notnull",
        }
        // Rank 9 — see ScopeRank: keywords sort after every real symbol, so a local/parameter/member
        // that happens to fuzzy-match the same letters doesn't
        // lose a tie-break to a keyword the user isn't actually reaching for. Priority matches —
        // lowest of all kinds, see ScopeRankPriority.
        .Select(kw => (ICompletionItem)new SimpleCompletionItem(
            kw, CompletionItemKind.Keyword, [new DisplayTextSegment(kw, SemanticTokenKind.Keyword)],
            Summary: null, SortText: $"9{kw}", Priority: 0.1, UsingsToAdd: [], Source: CompletionSourceKind.Keyword))
        .ToArray();

    // Contextual keywords with no legal meaning outside a specific syntax
    // position were being offered everywhere (e.g. "orderby"/"equals" inside an ordinary method body).
    // `where` is deliberately NOT included here — it's also the generic type-constraint clause keyword
    // (`where T : class`), which is legal in an ordinary member-declaration position, so filtering it
    // to "inside a query expression" would hide it somewhere it's actually valid.
    internal static readonly HashSet<string> _queryContinuationKeywords = new(StringComparer.Ordinal)
    {
        "from", "join", "into", "let", "orderby", "select", "group", "on", "equals",
        "ascending", "descending", "by",
    };

    // get/set/add/remove only mean something inside a property/indexer/event accessor list.
    // `value` only means something inside a set/add/remove accessor body (the implicit parameter name).
    internal static readonly HashSet<string> _accessorKeywords = new(StringComparer.Ordinal)
    {
        "get", "set", "add", "remove",
    };

    // The only keywords that are ever legal as a generic type argument are the predefined
    // type keywords (`List<int>`, `List<string>`, ...) plus `dynamic`/`nint`/`nuint` — every other
    // keyword in _keywords (control-flow, modifiers, LINQ, ...) is a syntax error there. `var`/`void`
    // are deliberately excluded: neither is a legal type argument (`List<var>`/`List<void>` don't
    // compile), unlike ordinary local-variable/return-type position where they're normally offered.
    internal static readonly HashSet<string> _typeArgumentKeywords = new(StringComparer.Ordinal)
    {
        "bool", "byte", "char", "decimal", "double", "dynamic", "float", "int", "long", "nint",
        "nuint", "object", "sbyte", "short", "string", "uint", "ulong", "ushort",
    };

    internal static bool IsKeywordApplicable(ICompletionItem keyword, SyntaxNode root, int position, bool isTypeArgumentPosition)
    {
        if (isTypeArgumentPosition)
            return _typeArgumentKeywords.Contains(keyword.DisplayText);

        if (_queryContinuationKeywords.Contains(keyword.DisplayText))
            return SourcePositions.IsInsideQueryExpression(root, position);

        if (keyword.DisplayText == "value")
            return SourcePositions.HasAncestorMatching(root, position, n =>
                n is AccessorDeclarationSyntax a && a.Kind() is SyntaxKind.SetAccessorDeclaration
                    or SyntaxKind.AddAccessorDeclaration or SyntaxKind.RemoveAccessorDeclaration);

        if (_accessorKeywords.Contains(keyword.DisplayText))
            return SourcePositions.IsInsideAccessorContext(root, position);

        return true;
    }

    // True anywhere inside a generic type argument list (`List<`, `List<Str`,
    // `Dictionary<string, `, ...) — the only place where completion should be restricted to type-ish
    // symbols instead of every in-scope symbol. Reuses the same HasAncestorMatching position-1
    // fallback as every other "what syntax is the caret inside" check in this file (see
    // IsInsideQueryExpression/IsInsideAccessorContext above) since the caret while typing sits at a
    // token's end, not inside it.
    internal static bool IsInsideTypeArgumentList(SyntaxNode root, int position) =>
        SourcePositions.HasAncestorMatching(root, position, n => n is TypeArgumentListSyntax);
}
