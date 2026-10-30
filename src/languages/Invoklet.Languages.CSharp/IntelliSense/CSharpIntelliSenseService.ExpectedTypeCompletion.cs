using Invoklet.Contracts.IntelliSense;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Invoklet.Languages.CSharp.IntelliSense;

// Completion where the code says what type is wanted: an argument of a call or constructor, or the
// value of a property in an object initializer. The wanted types come from the overloads that can
// still take the arguments typed so far; what doesn't fit them is left out, and what does is added
// (`new Type`, enum members, literals).
public sealed partial class CSharpIntelliSenseService
{
    // Types is never empty. AfterNew: the caret is where the type of a `new` expression goes.
    private sealed record ExpectedTypeContext(IReadOnlyList<ITypeSymbol> Types, bool AfterNew);

    private static ExpectedTypeContext? GetExpectedTypeContext(SemanticModel model, SyntaxNode root, int pos)
    {
        if (pos <= 0) return null;
        var token = root.FindToken(pos - 1);
        foreach (var node in token.Parent?.AncestorsAndSelf() ?? [])
        {
            switch (node)
            {
                case ArgumentListSyntax list when IsInsideArgumentList(list, pos):
                    return ForArgument(model, list, token, pos);
                case AssignmentExpressionSyntax
                {
                    Parent: InitializerExpressionSyntax { Parent: BaseObjectCreationExpressionSyntax } initializer,
                } assignment when initializer.IsKind(SyntaxKind.ObjectInitializerExpression)
                    && !assignment.OperatorToken.IsMissing && pos >= assignment.OperatorToken.Span.End:
                    return ForInitializerValue(model, initializer, assignment, pos);
                // Any other expression or statement around the caret (a lambda body, a nested
                // expression) wants a type this does not know.
                case LambdaExpressionSyntax or AnonymousFunctionExpressionSyntax or StatementSyntax:
                    return null;
            }
        }
        return null;
    }

    private static bool IsInsideArgumentList(ArgumentListSyntax list, int pos) =>
        pos >= list.OpenParenToken.Span.End && (list.CloseParenToken.IsMissing || pos <= list.CloseParenToken.SpanStart);

    private static ExpectedTypeContext? ForArgument(SemanticModel model, ArgumentListSyntax list, SyntaxToken token, int pos)
    {
        if (list.Parent is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)) return null;

        // The argument the caret is in, if one has been typed: `(` and `,` themselves belong to the list.
        var argument = token.Parent?.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault(a => a.Parent == list);
        var afterNew = false;
        if (argument is not null)
        {
            if (!argument.RefKindKeyword.IsKind(SyntaxKind.None) || !IsSimpleSlot(argument.Expression, pos, out afterNew)) return null;
        }

        var index = CountArgumentsBefore(list, pos);
        var name = argument?.NameColon?.Name.Identifier.ValueText;
        var types = new List<ITypeSymbol>();
        foreach (var method in GetCandidateMethods(model, list.Parent!))
        {
            if (!AcceptsTypedArguments(method, list, index, model)) continue;
            var parameter = name is null ? ParameterAt(method, index) : method.Parameters.FirstOrDefault(p => p.Name == name);
            if (parameter is null) continue;
            types.Add(parameter.Type);
            if (parameter.IsParams && parameter.Type is IArrayTypeSymbol array && name is null) types.Add(array.ElementType);
        }
        return Create(types, afterNew);
    }

    private static ExpectedTypeContext? ForInitializerValue(
        SemanticModel model, InitializerExpressionSyntax initializer, AssignmentExpressionSyntax assignment, int pos)
    {
        if (!IsSimpleSlot(assignment.Right, pos, out var afterNew)) return null;
        var member = model.GetSymbolInfo(assignment.Left).Symbol;
        if (member is null && assignment.Left is IdentifierNameSyntax id
            && model.GetTypeInfo(initializer.Parent!).Type is { } created)
            member = model.LookupSymbols(pos, container: created, name: id.Identifier.ValueText).FirstOrDefault();
        ITypeSymbol? type = member switch
        {
            IPropertySymbol { IsStatic: false, SetMethod: not null } p => p.Type,
            IFieldSymbol { IsStatic: false, IsReadOnly: false, IsConst: false } f => f.Type,
            _ => null,
        };
        return type is null ? null : Create([type], afterNew);
    }

    // What is being typed is a bare name or `new Name` (a longer expression wants more than the slot's type).
    private static bool IsSimpleSlot(ExpressionSyntax expression, int pos, out bool afterNew)
    {
        afterNew = false;
        switch (expression)
        {
            case IdentifierNameSyntax name:
                return name.IsMissing || (pos >= name.SpanStart && pos <= name.Span.End);
            case ObjectCreationExpressionSyntax { Type: IdentifierNameSyntax type, ArgumentList: null or { OpenParenToken.IsMissing: true }, Initializer: null } creation
                when pos >= creation.NewKeyword.Span.End && (type.IsMissing || pos <= type.Span.End):
                afterNew = true;
                return true;
            default:
                return false;
        }
    }

    // The overloads the arguments before the caret still fit: enough parameters, and each typed
    // argument converts to its parameter. An argument whose type isn't known yet counts as fitting.
    private static bool AcceptsTypedArguments(IMethodSymbol method, ArgumentListSyntax list, int index, SemanticModel model)
    {
        if (ParameterAt(method, index) is null && list.Arguments.All(a => a.NameColon is null)) return false;
        for (var i = 0; i < index && i < list.Arguments.Count; i++)
        {
            var argument = list.Arguments[i];
            if (argument.NameColon is not null) continue;
            var parameter = ParameterAt(method, i);
            if (parameter is null) return false;
            var argumentType = model.GetTypeInfo(argument.Expression).Type;
            if (argumentType is null or IErrorTypeSymbol) continue;
            if (!IsAssignable(model.Compilation, argumentType, parameter.Type)
                && !(parameter.IsParams && parameter.Type is IArrayTypeSymbol array && IsAssignable(model.Compilation, argumentType, array.ElementType)))
                return false;
        }
        return true;
    }

    private static IParameterSymbol? ParameterAt(IMethodSymbol method, int index)
    {
        if (index < method.Parameters.Length) return method.Parameters[index];
        return method.Parameters.Length > 0 && method.Parameters[^1].IsParams ? method.Parameters[^1] : null;
    }

    // Generic parameters of the overload (`T`, `List<T>`) are unknown until the arguments say what they are.
    private static ExpectedTypeContext? Create(IEnumerable<ITypeSymbol> types, bool afterNew)
    {
        var known = types
            .Where(t => t is not IErrorTypeSymbol && !ContainsTypeParameter(t))
            .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default)
            .ToList();
        return known.Count > 0 ? new ExpectedTypeContext(known, afterNew) : null;
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol array => ContainsTypeParameter(array.ElementType),
        IPointerTypeSymbol pointer => ContainsTypeParameter(pointer.PointedAtType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    private static bool IsAssignable(Compilation compilation, ITypeSymbol from, ITypeSymbol to) =>
        compilation.ClassifyConversion(from, to).IsImplicit;

    // The member types a value of which fits: a local, parameter, field or property is offered only
    // when its type converts to a wanted one. Everything else (methods, types, namespaces) stays: a
    // call or a member access may still produce the type.
    private static bool FitsExpectedType(ISymbol symbol, ExpectedTypeContext expected, SemanticModel model, int pos)
    {
        if (expected.AfterNew)
            return symbol switch
            {
                INamespaceSymbol => true,
                INamedTypeSymbol candidate => IsCreatable(candidate, model, pos)
                    && expected.Types.Any(t => CouldBeAssignedTo(model.Compilation, candidate, t)),
                _ => false,
            };

        var type = symbol switch
        {
            ILocalSymbol local => local.Type,
            IParameterSymbol parameter => parameter.Type,
            IFieldSymbol field => field.Type,
            IPropertySymbol property => property.Type,
            _ => null,
        };
        return type is null or IErrorTypeSymbol || expected.Types.Any(t => IsAssignable(model.Compilation, type, t));
    }

    // A class or struct `new` can make here. A generic type is tried against the wanted type by its
    // definition (List<T> for IList<int>), as its type arguments are not chosen yet.
    private static bool IsCreatable(INamedTypeSymbol type, SemanticModel model, int pos)
    {
        if (type.IsAbstract || type.IsStatic || type.TypeKind is not (TypeKind.Class or TypeKind.Struct)) return false;
        return (type.TypeKind == TypeKind.Struct && type.InstanceConstructors.All(c => c.IsImplicitlyDeclared))
            || type.InstanceConstructors.Any(c => model.IsAccessible(pos, c));
    }

    private static bool CouldBeAssignedTo(Compilation compilation, INamedTypeSymbol candidate, ITypeSymbol expected)
    {
        if (expected is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            expected = nullable.TypeArguments[0];
        if (!candidate.IsGenericType || candidate.TypeArguments.All(a => a is not ITypeParameterSymbol))
            return IsAssignable(compilation, candidate, expected);
        if (expected.SpecialType == SpecialType.System_Object) return true;
        if (expected is not INamedTypeSymbol wanted) return false;
        var definition = wanted.OriginalDefinition;
        return candidate.OriginalDefinition.AllInterfaces.Concat(BaseTypes(candidate.OriginalDefinition)).Prepend(candidate.OriginalDefinition)
            .Any(t => SymbolEqualityComparer.Default.Equals(t.OriginalDefinition, definition));
    }

    private static IEnumerable<INamedTypeSymbol> BaseTypes(INamedTypeSymbol type)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            yield return baseType;
    }

    // `null` where it can't be assigned and true/false where no bool is wanted would be wrong
    // offers; the other keywords can start any expression.
    private static bool FitsExpectedType(string keyword, ExpectedTypeContext expected, Compilation compilation)
    {
        if (expected.AfterNew) return false;
        return keyword switch
        {
            "null" => expected.Types.Any(CanBeNull),
            "true" or "false" => expected.Types.Any(t => IsAssignable(compilation, compilation.GetSpecialType(SpecialType.System_Boolean), t)),
            _ => true,
        };
    }

    private static bool CanBeNull(ITypeSymbol type) =>
        type.IsReferenceType || type.TypeKind == TypeKind.Pointer || type.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
            || type.TypeKind == TypeKind.TypeParameter;

    // What the wanted types suggest beyond the names in scope: `new Type`, the members of an enum
    // and a literal for the types written with one.
    private static IEnumerable<ICompletionItem> ExpectedTypeSuggestions(ExpectedTypeContext expected, SemanticModel model, int pos)
    {
        if (expected.AfterNew) yield break;
        foreach (var type in expected.Types)
        {
            var wanted = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
                ? nullable.TypeArguments[0]
                : type;
            if (wanted is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumType)
            {
                var display = enumType.ToMinimalDisplayString(model, pos);
                foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst && !f.IsImplicitlyDeclared))
                    yield return Suggestion($"{display}.{member.Name}", CompletionItemKind.EnumMember, member, null);
            }
            else if (wanted is INamedTypeSymbol { SpecialType: SpecialType.None } named && IsCreatable(named, model, pos))
            {
                var display = named.ToMinimalDisplayString(model, pos);
                yield return Suggestion("new " + display, named.TypeKind == TypeKind.Struct ? CompletionItemKind.Struct : CompletionItemKind.Class,
                    named, $"new {display}(${{0}})");
            }
            else if (Literal(wanted) is { } literal)
            {
                yield return new SimpleCompletionItem(literal.Text, CompletionItemKind.Keyword,
                    [new DisplayTextSegment(literal.Text, SemanticTokenKind.Keyword)], Summary: null,
                    SortText: "8" + literal.Text, Priority: 0.2, UsingsToAdd: [], InsertTemplate: literal.Template,
                    Source: CompletionSourceKind.ExpectedType);
            }
        }

        static ICompletionItem Suggestion(string text, CompletionItemKind kind, ISymbol symbol, string? template) =>
            new SimpleCompletionItem(text, kind, symbol.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetSummary(symbol),
                SortText: "0" + text, Priority: ScopeRankPriority(0), UsingsToAdd: [], InsertTemplate: template,
                Source: CompletionSourceKind.ExpectedType);
    }

    // `name:` for the parameters an argument can still name: of the overloads the typed arguments
    // fit, those not given by position or by name already. Only where a new argument starts — a
    // bare name or nothing typed yet, not after `name:` or inside a longer expression.
    private static IEnumerable<ICompletionItem> NamedArgumentSuggestions(SemanticModel model, SyntaxNode root, int pos)
    {
        if (pos <= 0) return [];
        var token = root.FindToken(pos - 1);
        var list = token.Parent?.AncestorsAndSelf().OfType<ArgumentListSyntax>().FirstOrDefault(l => IsInsideArgumentList(l, pos));
        if (list?.Parent is not (InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax)) return [];

        var argument = token.Parent?.AncestorsAndSelf().OfType<ArgumentSyntax>().FirstOrDefault(a => a.Parent == list);
        if (argument is not null && (argument.NameColon is not null || !argument.RefKindKeyword.IsKind(SyntaxKind.None)
            || argument.Expression is not IdentifierNameSyntax)) return [];
        // The caret has to be in the argument's name, not past it (`(x |`).
        if (argument is { Expression: IdentifierNameSyntax name } && !name.IsMissing && pos > name.Span.End) return [];

        var index = CountArgumentsBefore(list, pos);
        var positional = list.Arguments.Take(index).TakeWhile(a => a.NameColon is null).Count();
        var named = new HashSet<string>(
            list.Arguments.Where(a => a != argument).Select(a => a.NameColon?.Name.Identifier.ValueText).OfType<string>(),
            StringComparer.Ordinal);

        var items = new Dictionary<string, ICompletionItem>(StringComparer.Ordinal);
        foreach (var method in GetCandidateMethods(model, list.Parent))
        {
            if (!AcceptsTypedArguments(method, list, index, model)) continue;
            for (var i = positional; i < method.Parameters.Length; i++)
            {
                var parameter = method.Parameters[i];
                if (named.Contains(parameter.Name) || items.ContainsKey(parameter.Name)) continue;
                var text = parameter.Name + ":";
                items[parameter.Name] = new SimpleCompletionItem(text, CompletionItemKind.Variable,
                    parameter.ToDisplayParts(_briefFormat).ToSegments(), XmlDocSummary.GetParameterSummary(method, parameter.Name),
                    SortText: "0" + text, Priority: ScopeRankPriority(0), UsingsToAdd: [],
                    Source: CompletionSourceKind.NamedArgument);
            }
        }
        return items.Values;
    }

    // true/false are keywords already, and a character has no natural first value.
    private static (string Text, string? Template)? Literal(ITypeSymbol type) => type.SpecialType switch
    {
        SpecialType.System_String => ("\"\"", "\"${0}\""),
        SpecialType.System_SByte or SpecialType.System_Byte or SpecialType.System_Int16 or SpecialType.System_UInt16
            or SpecialType.System_Int32 or SpecialType.System_UInt32 or SpecialType.System_Int64 or SpecialType.System_UInt64
            or SpecialType.System_Single or SpecialType.System_Double or SpecialType.System_Decimal => ("0", null),
        _ => null,
    };

    // The methods or constructors a call can mean. Roslyn fills CandidateSymbols only when binding is
    // ambiguous, so a valid call would show just the one overload it resolved to: the whole member or
    // constructor group is read instead, and only a call with no group (through a delegate) keeps what
    // binding found. Shared by signature help and the expected-type completion.
    private static List<IMethodSymbol> GetCandidateMethods(SemanticModel model, SyntaxNode callLike)
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
