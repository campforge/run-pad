using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят первый проход, batch) — own pick, not a Roslynator port.
// `catch (Exception ex) when (ex is FooException)` catches broadly and then narrows with a type test
// in the filter — `catch (FooException ex)` says the same thing directly, at the language level,
// without needing a filter at all. Deliberately conservative: the filter's condition must be EXACTLY a
// bare `x is Type` test against the caught variable (no `&&`/`||` compounding it with anything else),
// and the tested type must actually be the declared type or a subtype of it — otherwise the filter is
// doing something this analyzer doesn't understand well enough to suggest collapsing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferSpecificCatchTypeOverIsPatternAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0072";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer a specific catch type over a when-filter type check",
        messageFormat: "Catch '{0}' directly instead of catching a broader type and checking 'is {0}' in a when-filter",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A when-filter that only tests the caught variable's type says the same thing as catching that type directly, without the extra indirection.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.CatchClause);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var catchClause = (CatchClauseSyntax)context.Node;
        if (catchClause.Declaration is not { } declaration || declaration.Identifier.IsMissing) return;
        if (catchClause.Filter is not
            {
                FilterExpression: BinaryExpressionSyntax
                {
                    RawKind: (int)SyntaxKind.IsExpression,
                    Left: IdentifierNameSyntax leftIdentifier,
                    Right: TypeSyntax patternType,
                },
            } filter)
        {
            return;
        }
        if (leftIdentifier.Identifier.Text != declaration.Identifier.Text) return;

        var ct = context.CancellationToken;
        var declaredType = context.SemanticModel.GetTypeInfo(declaration.Type, ct).Type;
        var narrowedType = context.SemanticModel.GetTypeInfo(patternType, ct).Type;
        if (declaredType is null || narrowedType is null) return;
        if (SymbolEqualityComparer.Default.Equals(declaredType, narrowedType)) return; // degenerate, not this analyzer's business
        if (!IsSameOrSubtype(narrowedType, declaredType)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, filter.GetLocation(), narrowedType.Name));
    }

    private static bool IsSameOrSubtype(ITypeSymbol type, ITypeSymbol baseType)
    {
        for (var current = type; current is not null; current = current.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(current, baseType)) return true;
        }
        return false;
    }
}
