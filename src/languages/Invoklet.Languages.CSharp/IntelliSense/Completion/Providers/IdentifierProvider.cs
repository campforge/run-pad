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

// Anywhere else: the names in scope and the keywords that fit, narrowed to types inside `List<`
// and to what fits where the code wants a type, plus types from references not imported yet.
internal sealed class IdentifierProvider(ScriptContext context) : ICompletionProvider
{
    public SimpleCompletionList? Provide(CompletionRequest request)
    {
        var model = request.Model;
        var root = request.Root;
        var pos = request.Offset;
        var expected = request.ExpectedType;
        var isTypeArgumentPosition = IsInsideTypeArgumentList(root, pos);

        // Namespaces stay in a type argument so `List<Sys` can descend into System.
        IEnumerable<ISymbol> symbols = model.LookupSymbols(pos);
        if (isTypeArgumentPosition)
        {
            symbols = symbols.Where(s => s is INamedTypeSymbol or ITypeParameterSymbol or INamespaceSymbol);
            expected = null;
        }
        else if (expected is not null)
        {
            symbols = symbols.Where(s => FitsExpectedType(s, expected, model, pos));
        }

        var currentType = GetEnclosingType(model, pos);
        var visible = GroupByDisplayText(symbols
            .Select(s => ToItem(s, currentType))
            .Where(i => i != null)
            .Cast<ICompletionItem>()
            .Concat(_keywords.Where(k => IsKeywordApplicable(k, root, pos, isTypeArgumentPosition)
                && (expected is null || FitsExpectedType(k.DisplayText, expected, model.Compilation))))
            .Concat(expected is null ? [] : ExpectedTypeSuggestions(expected, model, pos))
            .Concat(isTypeArgumentPosition || expected is { AfterNew: true } ? [] : NamedArgumentSuggestions(model, root, pos)));

        // Types from the references not in scope yet, prefix-matched; accepting one adds its using
        // (UsingsToAdd).
        var prefix = GetIdentifierPrefix(request.Code, request.Position);
        var alreadyVisible = new HashSet<string>(visible.Select(i => i.DisplayText), StringComparer.Ordinal);
        var importable = FindImportableTypes(context, model.Compilation, prefix, alreadyVisible)
            .Where(t => expected is not { AfterNew: true } || (IsCreatable(t, model, pos)
                && expected.Types.Any(w => CouldBeAssignedTo(model.Compilation, t, w))))
            .Select(BuildImportableTypeItem);

        var items = visible.Concat(importable)
            .OrderBy(i => i.SortText, StringComparer.Ordinal)
            .ToList();
        var kind = isTypeArgumentPosition ? CompletionContextKind.TypeArgument
            : expected is { AfterNew: true } ? CompletionContextKind.ExpectedNewType
            : expected is not null ? CompletionContextKind.ExpectedValue
            : CompletionContextKind.Identifier;
        return new SimpleCompletionList(items, kind);
    }
}
