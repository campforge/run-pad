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

/// <summary>Builds the items of a completion list from symbols and ranks them: own members first, then by scope, keywords last.</summary>
internal static class CompletionItems
{
    // Collapses same-named candidates down to one item per DisplayText (the same instance already
    // won by ScopeRank/Kind ordering everywhere this was called before), stamping the survivor with
    // OverloadCount when more than one Method-kind candidate shares the name (
    // without this, accepting a name with several overloads gave no hint there was anything else
    // under it at all) and with IsOwnMember if ANY grouped candidate is declared directly on the
    // current/receiver type — e.g. Derived.Foo(int) shadowing
    // Base.Foo(string) should still render bold even though ScopeRank/Kind ordering might pick the
    // base one as the group's representative.
    internal static IReadOnlyList<ICompletionItem> GroupByDisplayText(IEnumerable<ICompletionItem> items) =>
        items
            .GroupBy(i => i.DisplayText)
            .Select(g =>
            {
                var chosen = g.OrderBy(i => (int)i.Kind).First();
                if (chosen is not SimpleCompletionItem simple) return chosen;

                var overloadCount = g.Count(i => i.Kind is CompletionItemKind.Method or CompletionItemKind.ExtensionMethod);
                var isOwnMember = g.Any(i => i.IsOwnMember);
                return simple with
                {
                    OverloadCount = overloadCount > 1 ? overloadCount : simple.OverloadCount,
                    IsOwnMember = isOwnMember,
                };
            })
            .ToList();

    // The INamedTypeSymbol containing the given position — used for the general (non-dot) completion
    // branch's "is this an own member" check. Walks up from
    // GetEnclosingSymbol rather than assuming a fixed nesting depth, since top-level statements (this
    // project's primary script shape) land inside a compiler-synthesized Program class one level
    // deeper than an ordinary explicit class body would.
    internal static INamedTypeSymbol? GetEnclosingType(SemanticModel model, int position)
    {
        var symbol = model.GetEnclosingSymbol(position);
        while (symbol is not null and not INamedTypeSymbol)
            symbol = symbol.ContainingSymbol;
        return symbol as INamedTypeSymbol;
    }

    internal static bool IsOwnMember(ISymbol symbol, ITypeSymbol? currentType) =>
        currentType is not null && SymbolEqualityComparer.Default.Equals(symbol.ContainingType, currentType);

    internal static ICompletionItem? ToItem(ISymbol symbol, ITypeSymbol? currentType) => symbol switch
    {
        // LocalFunction alongside Ordinary/ReducedExtension — top-level statements (this project's
        // main script shape) use local functions as their primary way to structure code, so leaving
        // them out meant the single most common non-trivial identifier in a script never completed
        //.
        IMethodSymbol m when m.MethodKind is MethodKind.ReducedExtension
            => BuildExtensionMethodItem(m, currentType),
        IMethodSymbol m when m.MethodKind is MethodKind.Ordinary or MethodKind.LocalFunction
            => BuildItem(m, m.Name, CompletionItemKind.Method, IsOwnMember(m, currentType)),
        IPropertySymbol p
            => BuildItem(p, p.Name, CompletionItemKind.Property, IsOwnMember(p, currentType)),
        IFieldSymbol f when !f.IsImplicitlyDeclared
            => BuildItem(f, f.Name,
                f.ContainingType?.TypeKind == TypeKind.Enum ? CompletionItemKind.EnumMember : CompletionItemKind.Field,
                IsOwnMember(f, currentType)),
        IEventSymbol e
            => BuildItem(e, e.Name, CompletionItemKind.Event, IsOwnMember(e, currentType)),
        ILocalSymbol l
            => BuildItem(l, l.Name, CompletionItemKind.Variable),
        IParameterSymbol p
            => BuildItem(p, p.Name, CompletionItemKind.Variable),
        INamedTypeSymbol t
            => BuildItem(t, t.Name, t.TypeKind switch
            {
                TypeKind.Interface => CompletionItemKind.Interface,
                TypeKind.Enum => CompletionItemKind.Enum,
                TypeKind.Struct => CompletionItemKind.Struct,
                TypeKind.Delegate => CompletionItemKind.Delegate,
                _ => CompletionItemKind.Class,
            }),
        // Type parameters (the `T` in `void Foo<T>(T value)`) were dropped entirely — inside a
        // generic method/type's own body, `T` is a real, commonly-referenced symbol,
        // not just declaration-site syntax.
        ITypeParameterSymbol tp
            => BuildItem(tp, tp.Name, CompletionItemKind.TypeParameter),
        // Namespaces were silently dropped here despite ScopeRank already carrying a rank for them
        // (3 — below types) — LookupSymbols(pos) does return INamespaceSymbol candidates (e.g. typing
        // "Sys" offers "System" in real VS/Rider), they just never became a completion item
        //. CompletionItemKind has no dedicated "Namespace" entry — Module is the
        // closest LSP-style stand-in (VS Code/LSP itself uses Module for both).
        INamespaceSymbol ns
            => BuildItem(ns, ns.Name, CompletionItemKind.Module),
        _ => null
    };

    internal static ICompletionItem BuildItem(
        ISymbol symbol, string displayText, CompletionItemKind kind, bool isOwnMember = false)
    {
        var rank = ScopeRank(symbol);
        return new SimpleCompletionItem(displayText, kind,
            symbol.ToDisplayParts(SymbolFormats.Brief).ToSegments(), XmlDocSummary.GetSummary(symbol),
            SortText: $"{rank}{displayText}", Priority: ScopeRankPriority(rank), UsingsToAdd: [],
            IsOwnMember: isOwnMember);
    }

    internal static ICompletionItem BuildExtensionMethodItem(IMethodSymbol method, ITypeSymbol? currentType)
    {
        var item = (SimpleCompletionItem)BuildItem(method, method.Name, CompletionItemKind.ExtensionMethod,
            IsOwnMember(method, currentType));
        // Invoklet's own extensions (Dump and the like) win a tie with an equally matching member: typing
        // `.D` on any value most often means Dump. The list order itself stays alphabetical.
        return IsInvokletRuntimeMember(method)
            ? item with { Priority = Math.Max(1.1, item.Priority), Source = CompletionSourceKind.Invoklet }
            : item with { Priority = Math.Max(0.85, item.Priority) };
    }

    internal static bool IsInvokletRuntimeMember(ISymbol symbol) =>
        symbol.ContainingAssembly?.Name == "Invoklet.Runtime";

    // A type from a reference that isn't in
    // scope yet. Own tier (ScopeRank 3, between ordinary types and namespaces) rather than sharing
    // rank 2 with already-visible types, and carries Detail (the containing namespace) so the
    // completion list itself shows the ReSharper-style "(in Namespace)" annotation — see
    // EditorTabItemControl's completion-item construction. UsingsToAdd is what tells
    // EditorTabItemControl to add the `using` automatically when this item is accepted (see
    // CompletionRequestedEventArgs's consumer in EditorTabItemControl.AsyncCompletionProvider).
    internal static ICompletionItem BuildImportableTypeItem(INamedTypeSymbol type)
    {
        var kind = type.TypeKind switch
        {
            TypeKind.Interface => CompletionItemKind.Interface,
            TypeKind.Enum => CompletionItemKind.Enum,
            TypeKind.Struct => CompletionItemKind.Struct,
            TypeKind.Delegate => CompletionItemKind.Delegate,
            _ => CompletionItemKind.Class,
        };
        // A type in the global namespace needs no "using" to become visible — treat it as needing no
        // import rather than emitting an empty namespace, which would otherwise surface as "(in )" in
        // the list and add an empty entry to the .inv front matter's namespaces (later producing an
        // invalid "using ;").
        var ns = type.ContainingNamespace.IsGlobalNamespace ? null : type.ContainingNamespace.ToDisplayString();

        const int rank = 3; // own tier, see ScopeRank
        return new SimpleCompletionItem(
            type.Name, kind, type.ToDisplayParts(SymbolFormats.Brief).ToSegments(), XmlDocSummary.GetSummary(type),
            SortText: $"{rank}{type.Name}", Priority: ScopeRankPriority(rank),
            UsingsToAdd: ns is null ? [] : [ns], Detail: ns, Source: CompletionSourceKind.ImportableType);
    }

    // Walks back from position over identifier characters in the user's own code (not the
    // preamble-prefixed document Roslyn sees) — the same "what word is being typed" idea as
    // CodeTextEditor.GetWordStart, just server-side and string-only (no syntax tree needed for this).
    internal static string GetIdentifierPrefix(string code, int position)
    {
        var end = Math.Clamp(position, 0, code.Length);
        var start = end;
        while (start > 0 && (char.IsLetterOrDigit(code[start - 1]) || code[start - 1] == '_'))
            start--;
        return code[start..end];
    }

    // Locals/parameters (the innermost scope) sort before members of the containing type, which sort
    // before other (in-scope) types, which sort before not-yet-imported types (rank 3, see
    // BuildImportableTypeItem), which
    // sort before namespaces — so typing a few letters shows what's actually nearby before it shows
    // something merely imported/visible, and before something that isn't even imported yet
    // ("the in-scope thing first, then a level up").
    internal static int ScopeRank(ISymbol symbol) => symbol switch
    {
        ILocalSymbol or IParameterSymbol => 0,
        IFieldSymbol or IPropertySymbol or IEventSymbol or IMethodSymbol => 1,
        INamedTypeSymbol => 2,
        INamespaceSymbol => 4,
        _ => 2,
    };

    // Whether a member found via LookupSymbols(container: type) actually belongs on the side of the
    // dot it was requested for — "DateTime." (static qualifier) vs "d." (instance qualifier).
    // Nested types are only ever reachable through the type itself (there's no such
    // thing as `instance.NestedType`), regardless of the nested type's own staticness, hence the
    // special case rather than just comparing symbol.IsStatic for everything.
    internal static bool MatchesQualifierStaticness(ISymbol symbol, bool isStaticQualifier) => symbol switch
    {
        INamedTypeSymbol => isStaticQualifier,
        _ => symbol.IsStatic == isStaticQualifier,
    };

    // Generated script APIs are intentionally treated as first-class completion surfaces.  Their
    // members come from the current execution context (Arguments/Connections) or the compatibility
    // runtime (Util), so they should win ties against similarly named ordinary symbols while still
    // retaining the normal alphabetical order inside this member list.
    internal static bool IsGeneratedApiRoot(ExpressionSyntax qualifier) =>
        qualifier is IdentifierNameSyntax { Identifier.ValueText: "Arguments" or "Connections" or "Util" };

    internal static ICompletionItem PromoteGeneratedApiItem(ICompletionItem item) => item switch
    {
        SimpleCompletionItem simple => simple with
        {
            SortText = $"0{simple.SortText}",
            Priority = Math.Max(1.1, simple.Priority),
            Source = CompletionSourceKind.Invoklet,
        },
        _ => item,
    };

    // ICompletionItem.Priority — the editor's tie-break among items of otherwise-equal match quality
    // (see AvaloniaEdit.CodeCompletion.CompletionList.SelectItem: quality decides first, Priority only
    // breaks ties). Derived from the same ScopeRank used for SortText, rather than the UI layer's
    // former Kind == Method ? 1.0 : 0.9 hardcode — so a local sitting right next to
    // an equally-good-matching method actually wins the tie-break its ScopeRank already implies.
    internal static double ScopeRankPriority(int rank) => rank switch
    {
        0 => 1.0,  // locals/parameters
        1 => 0.8,  // members
        2 => 0.6,  // types
        3 => 0.5,  // importable (not-yet-imported) types
        _ => 0.4,  // namespaces
    };
}
