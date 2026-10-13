using System.Collections.Generic;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A correctness
// finding: `new Dictionary<K, V> { { key1, v1 }, { key2, v2 }, ... }` — the `{ key, value }` element
// form of a collection initializer desugars to a sequence of `Add(key, value)` calls, and
// `Dictionary<K,V>.Add` throws `ArgumentException` if the key already exists — so a literal key
// repeated across two elements always throws at runtime, the moment the dictionary is constructed.
// Deliberately does NOT cover the indexer-assignment form (`{ [key1] = v1, [key2] = v2 }`) — that
// desugars to plain indexer assignments, which overwrite rather than throw, so a repeated key there is
// legal (if probably still a mistake, it's a silent one, not a "this always throws" one — a different
// analyzer's territory, not this one's). Scoped to keys with a provable CONSTANT value (so two
// differently-spelled expressions that happen to evaluate to the same constant, e.g. `1 + 1` and `2`,
// are still caught) — two keys that are merely the same variable read twice aren't assumed to be
// constant-equal.
// Extended to also catch string keys that differ only in CASE
// when the dictionary was constructed with one of the three built-in case-insensitive comparers
// (`StringComparer.OrdinalIgnoreCase`/`InvariantCultureIgnoreCase`/`CurrentCultureIgnoreCase`) — under
// one of those, `"a"` and `"A"` collide too, the same `Add` throw as an exact duplicate. Detected via a
// literal `StringComparer.XxxIgnoreCase` member-access as the constructor's first argument — a
// comparer coming from a variable/field isn't assumed to be case-insensitive.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DuplicateKeyInDictionaryInitializerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0241";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Duplicate key in a Dictionary collection initializer",
        messageFormat: "This key duplicates an earlier entry in the same initializer — the Add(key, value) form always throws ArgumentException for a repeated key, the moment the dictionary is constructed",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The { key, value } collection-initializer form calls Dictionary<K,V>.Add for each element, which throws ArgumentException if the key was already added earlier in the same initializer.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.Initializer is not { RawKind: (int)SyntaxKind.CollectionInitializerExpression } initializer)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetTypeInfo(creation, ct).Type is not INamedTypeSymbol type) return;
        if (type.OriginalDefinition.ToDisplayString() != "System.Collections.Generic.Dictionary<TKey, TValue>")
            return;

        var caseInsensitive = HasCaseInsensitiveComparerArgument(creation);

        var seenKeys = new Dictionary<object, bool>();
        foreach (var element in initializer.Expressions)
        {
            if (element is not InitializerExpressionSyntax { Expressions: [var keyExpr, _] }) continue;

            var constant = context.SemanticModel.GetConstantValue(keyExpr, ct);
            if (!constant.HasValue || constant.Value is null) continue;

            var key = caseInsensitive && constant.Value is string stringValue
                ? stringValue.ToUpperInvariant()
                : constant.Value;

            if (!seenKeys.TryAdd(key, true))
                context.ReportDiagnostic(Diagnostic.Create(Rule, keyExpr.GetLocation()));
        }
    }

    private static readonly ImmutableHashSet<string> CaseInsensitiveComparerNames = ImmutableHashSet.Create(
        "OrdinalIgnoreCase", "InvariantCultureIgnoreCase", "CurrentCultureIgnoreCase");

    private static bool HasCaseInsensitiveComparerArgument(BaseObjectCreationExpressionSyntax creation) =>
        creation.ArgumentList is { Arguments: [{ Expression: MemberAccessExpressionSyntax { Expression: IdentifierNameSyntax { Identifier.Text: "StringComparer" }, Name.Identifier.Text: var comparerName } }, ..] }
        && CaseInsensitiveComparerNames.Contains(comparerName);
}
