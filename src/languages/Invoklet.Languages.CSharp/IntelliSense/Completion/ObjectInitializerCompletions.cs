using Invoklet.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Invoklet.Languages.CSharp.IntelliSense.Completion.CompletionItems;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion;

/// <summary>The members offered inside an object initializer.</summary>
internal static class ObjectInitializerCompletions
{
    // The nearest ObjectInitializerExpression ancestor of the token right before the
    // caret, same "walk up from the token before pos" idiom the dot/member-access branch above uses
    // for MemberAccessExpressionSyntax. Right after '{' or ',' (the common case — nothing typed yet
    // for the next member), that token's own Parent already IS the InitializerExpressionSyntax (open
    // brace and the SeparatedSyntaxList's separators are both direct children of it); mid-typing a
    // member name, the token's parent chain still leads up through it.
    internal static InitializerExpressionSyntax? TryFindObjectInitializer(SyntaxNode root, int pos)
    {
        if (pos <= 0) return null;
        var token = root.FindToken(pos - 1);
        return token.Parent?.AncestorsAndSelf()
            .OfType<InitializerExpressionSyntax>()
            .FirstOrDefault(i => i.IsKind(SyntaxKind.ObjectInitializerExpression));
    }

    // GetTypeInfo on the object-creation EXPRESSION itself, not its .Type child node — same Roslyn
    // quirk noted in TryGetSignatureHelpAsync below: the latter comes back null for a type declared
    // in the same document being analyzed.
    internal static List<ICompletionItem> BuildObjectInitializerCompletions(
        SemanticModel model, int pos, BaseObjectCreationExpressionSyntax objectCreation,
        InitializerExpressionSyntax initializer)
    {
        if (model.GetTypeInfo(objectCreation).Type is not INamedTypeSymbol type) return [];

        // Already-assigned members ("P1 = " already typed) shouldn't be offered again — LookupSymbols
        // has no idea what's already been set inside THIS particular initializer, only what's a
        // legal member of the type in general.
        var alreadyAssigned = new HashSet<string>(
            initializer.Expressions.OfType<AssignmentExpressionSyntax>()
                .Select(a => a.Left).OfType<IdentifierNameSyntax>()
                .Select(id => id.Identifier.ValueText),
            StringComparer.Ordinal);

        // LookupSymbols(pos, container: type) — same API the dot-member-access branch above uses —
        // already filters to what's accessible from this position, so no separate accessibility check
        // is needed here. Narrowed further to actually-settable members: a property needs a setter
        // (SetMethod is non-null for both `set` and `init` accessors — `with`-only init-only members
        // are still legitimately settable from an object initializer), a field needs to not be
        // readonly/const, and neither can be static (an object initializer only ever targets instance
        // members of the freshly-created instance).
        var members = model.LookupSymbols(pos, container: type)
            .Where(s => s switch
            {
                IPropertySymbol { IsStatic: false, SetMethod: not null } => true,
                IFieldSymbol { IsStatic: false, IsReadOnly: false, IsConst: false } => true,
                _ => false,
            })
            .Where(s => !alreadyAssigned.Contains(s.Name))
            .Select(s => ToItem(s, currentType: type))
            .Where(i => i != null)
            .Cast<ICompletionItem>();

        return GroupByDisplayText(members).OrderBy(i => i.SortText, StringComparer.Ordinal).ToList();
    }
}
