using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RunPad.Contracts.IntelliSense;

namespace RunPad.Languages.CSharp.IntelliSense;

// Completion is answered by a list of providers, one per kind of place in the code, asked in order:
// the first that recognizes the caret's place answers, even with nothing to offer. Each provider
// keeps its own rules, and what a list was built for (CompletionContextKind) and where each item
// came from (CompletionSourceKind) stay visible to the tests.
public sealed partial class CSharpIntelliSenseService
{
    private ICompletionProvider[]? _completionProviders;

    private ICompletionProvider[] CompletionProviders => _completionProviders ??=
    [
        new ArgumentsContractProvider(this),
        new OverrideStubProvider(),
        new NoCompletionProvider(),
        new MemberAccessProvider(),
        new ObjectInitializerProvider(),
        new IdentifierProvider(this),
    ];

    private SimpleCompletionList? ProvideCompletions(CompletionRequest request)
    {
        foreach (var provider in CompletionProviders)
        {
            if (provider.Provide(request) is not { } list) continue;
            return list.Items.Count > 0 ? list : null;
        }
        return null;
    }

    private interface ICompletionProvider
    {
        // Null when the caret is not in this provider's kind of place, so the next one is asked; a
        // list, even an empty one, answers the request.
        SimpleCompletionList? Provide(CompletionRequest request);
    }

    // One completion request: the document the caret is in, read once and shared by the providers.
    private sealed class CompletionRequest(
        string code, int position, CompletionTrigger trigger, SemanticModel model, SyntaxNode root, int documentOffset)
    {
        private ExpectedTypeContext? _expectedType;
        private bool _expectedTypeRead;

        // The caret in the user's code and in the document Roslyn sees (behind the preamble).
        public string Code => code;
        public int Position => position;
        public int Offset => documentOffset;
        public CompletionTrigger Trigger => trigger;
        public SemanticModel Model => model;
        public SyntaxNode Root => root;

        public string BeforeCaret => code[..Math.Min(position, code.Length)];

        public bool IsDotInsertion => trigger.Kind == CompletionTriggerKind.Insertion && trigger.Character == '.';

        // What type the code wants at the caret, read only by the providers that need it.
        public ExpectedTypeContext? ExpectedType
        {
            get
            {
                if (!_expectedTypeRead)
                {
                    _expectedType = GetExpectedTypeContext(model, root, documentOffset);
                    _expectedTypeRead = true;
                }
                return _expectedType;
            }
        }
    }

    // `Arguments.` lists the script's argument contract straight from its metadata, so it works
    // before a compile has produced the generated Arguments class.
    private sealed class ArgumentsContractProvider(CSharpIntelliSenseService service) : ICompletionProvider
    {
        private const string Root = "Arguments.";

        public SimpleCompletionList? Provide(CompletionRequest request)
        {
            var beforeCaret = request.BeforeCaret;
            var start = beforeCaret.LastIndexOf(Root, StringComparison.Ordinal);
            if (start < 0 || (start > 0 && char.IsLetterOrDigit(beforeCaret[start - 1]))) return null;
            var typed = beforeCaret[(start + Root.Length)..];
            if (!typed.All(char.IsLetterOrDigit)) return null;

            var items = service._argumentContract
                .Where(m => m.PropertyName.StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .Select(m => (ICompletionItem)new SimpleCompletionItem(
                    m.PropertyName, CompletionItemKind.Property,
                    [new DisplayTextSegment(m.PropertyName, SemanticTokenKind.PropertyName)],
                    $"{m.PropertyName} : {(m.Type == "enum" ? m.EnumTypeName : m.Type)}", m.PropertyName, 1, [],
                    Source: CompletionSourceKind.RunPad))
                .ToList();
            // A contract with no such member leaves the place to Roslyn (a user's own `Arguments`).
            return items.Count > 0 ? new SimpleCompletionList(items, CompletionContextKind.ArgumentsContract) : null;
        }
    }

    // "override " lists the base members still open to override, each inserted as a whole stub.
    // Checked before the syntax guards: the keyword can only be a modifier, so the incomplete
    // declaration after it does not need to parse as anything in particular.
    private sealed class OverrideStubProvider : ICompletionProvider
    {
        public SimpleCompletionList? Provide(CompletionRequest request)
        {
            if (!IsAfterOverrideModifier(request.Code, request.Position)) return null;

            var type = GetEnclosingType(request.Model, request.Offset);
            var items = type?.BaseType is null
                ? []
                : GetOverridableMethods(type)
                    .Select(m => BuildOverrideStubItem(m, request.Model, request.Offset))
                    .OrderBy(i => i.SortText, StringComparer.Ordinal)
                    .ToList();
            return new SimpleCompletionList(items, CompletionContextKind.OverrideStub);
        }
    }

    // Comments, string literals and the name of a declaration being typed take no completion.
    private sealed class NoCompletionProvider : ICompletionProvider
    {
        public SimpleCompletionList? Provide(CompletionRequest request)
        {
            var root = request.Root;
            var pos = request.Offset;
            return IsInsideComment(root, pos) || IsInsideStringOrCharLiteral(root, pos) || IsDeclarationNamePosition(root, pos)
                ? new SimpleCompletionList([], CompletionContextKind.None)
                : null;
        }
    }

    // After `x.`, `x?.`, `Type.` or `Namespace.` — and on an explicit invoke right after a dot, which
    // behaves like typing the dot rather than listing every name and keyword in scope.
    private sealed class MemberAccessProvider : ICompletionProvider
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

    // `new Foo { X = 1, | }` lists Foo's settable members not assigned yet, not what is in scope.
    // Only the direct `new Foo { }` shape: a nested initializer without its own `new` and `with { }`
    // are left to the general list. A member's value (`P = |`) takes the expected-type completion.
    private sealed class ObjectInitializerProvider : ICompletionProvider
    {
        public SimpleCompletionList? Provide(CompletionRequest request)
        {
            if (request.ExpectedType is not null
                || TryFindObjectInitializer(request.Root, request.Offset) is not { } initializer
                || initializer.Parent is not BaseObjectCreationExpressionSyntax objectCreation)
                return null;
            var items = BuildObjectInitializerCompletions(request.Model, request.Offset, objectCreation, initializer);
            return new SimpleCompletionList(items, CompletionContextKind.ObjectInitializerMember);
        }
    }

    // Anywhere else: the names in scope and the keywords that fit, narrowed to types inside `List<`
    // and to what fits where the code wants a type, plus types from references not imported yet.
    private sealed class IdentifierProvider(CSharpIntelliSenseService service) : ICompletionProvider
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
            var importable = service.FindImportableTypes(model.Compilation, prefix, alreadyVisible)
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
}
