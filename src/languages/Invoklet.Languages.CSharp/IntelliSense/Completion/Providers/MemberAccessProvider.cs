using Invoklet.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using static Invoklet.Languages.CSharp.IntelliSense.Completion.CompletionItems;

namespace Invoklet.Languages.CSharp.IntelliSense.Completion.Providers;

// After `x.`, `x?.`, `Type.` or `Namespace.` — and on an explicit invoke right after a dot, which
// behaves like typing the dot rather than listing every name and keyword in scope.
internal sealed class MemberAccessProvider : ICompletionProvider
{
    public SimpleCompletionList? Provide(CompletionRequest request)
    {
        var pos = request.Offset;
        if (!(request.IsDotInsertion || request.Trigger.Kind == CompletionTriggerKind.Invoked) || pos <= 0) return null;

        var model = request.Model;
        var token = request.Root.FindToken(pos - 1);
        // x?.Foo: the '.' belongs to a MemberBindingExpression, whose qualifier is on the
        // ConditionalAccessExpression above it.
        var memberAccess = token.Parent?.AncestorsAndSelf().OfType<MemberAccessExpressionSyntax>().FirstOrDefault();
        var memberBinding = token.Parent?.AncestorsAndSelf().OfType<MemberBindingExpressionSyntax>().FirstOrDefault();
        var qualifier = memberAccess?.Expression
            ?? (memberBinding?.Parent as ConditionalAccessExpressionSyntax)?.Expression;

        if (qualifier != null)
        {
            var qualifierSymbol = model.GetSymbolInfo(qualifier).Symbol;

            // "System.": a namespace has no type for GetTypeInfo; its namespaces and types are listed.
            if (qualifierSymbol is INamespaceSymbol containingNamespace)
            {
                var namespaceItems = GroupByDisplayText(model.LookupNamespacesAndTypes(pos, container: containingNamespace)
                        .Select(s => ToItem(s, currentType: null))
                        .Where(i => i != null)
                        .Cast<ICompletionItem>())
                    .OrderBy(i => i.SortText, StringComparer.Ordinal)
                    .ToList();
                return new SimpleCompletionList(namespaceItems, CompletionContextKind.NamespaceMember);
            }

            // "DateTime." names the type itself (static members); "d." is a value (instance
            // members). Only GetSymbolInfo tells them apart.
            var isStaticQualifier = qualifierSymbol is INamedTypeSymbol;
            ITypeSymbol? type;
            if (isStaticQualifier)
            {
                type = (ITypeSymbol)qualifierSymbol!;
            }
            else
            {
                var typeInfo = model.GetTypeInfo(qualifier);
                type = typeInfo.Type ?? typeInfo.ConvertedType;
            }

            if (type != null)
            {
                var generatedApiRoot = IsGeneratedApiRoot(qualifier);
                var beforeCaret = request.BeforeCaret;
                var memberPrefix = generatedApiRoot ? beforeCaret[(beforeCaret.LastIndexOf('.') + 1)..] : "";
                // LookupSymbols(container: type) returns static and instance members alike.
                // Extension methods only apply to an instance.
                var memberItems = GroupByDisplayText(model
                        .LookupSymbols(pos, container: type, includeReducedExtensionMethods: !isStaticQualifier)
                        .Where(s => MatchesQualifierStaticness(s, isStaticQualifier))
                        .Select(s => ToItem(s, currentType: type))
                        .Where(i => i != null)
                        .Cast<ICompletionItem>())
                    .Where(i => !generatedApiRoot || i.DisplayText.StartsWith(memberPrefix, StringComparison.OrdinalIgnoreCase))
                    .Select(i => generatedApiRoot ? PromoteGeneratedApiItem(i) : i)
                    .OrderBy(i => i.SortText, StringComparer.Ordinal)
                    .ToList();
                var kind = generatedApiRoot ? CompletionContextKind.GeneratedApiMember
                    : isStaticQualifier ? CompletionContextKind.StaticMember
                    : CompletionContextKind.InstanceMember;
                return new SimpleCompletionList(memberItems, kind);
            }
        }

        // A typed '.' with nothing to complete has nothing to fall back to; an explicit invoke
        // elsewhere goes on to the general list.
        return request.IsDotInsertion ? new SimpleCompletionList([], CompletionContextKind.None) : null;
    }
}
