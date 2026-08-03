using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести семьдесят шестой проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231): the
// `Version(int major, int minor[, int build[, int revision]])` constructors throw
// ArgumentOutOfRangeException when any component is negative (confirmed empirically before writing
// this). A separate analyzer rather than an extension of InvalidVersionLiteralFormatAnalyzer
// (RPAD0247, `Version.Parse` string-format validation) — same TYPE, but a completely different
// checking mechanism (literal-sign check on int constructor arguments vs. regex on a string), matching
// this project's precedent of keeping CultureSensitiveNumericParseAnalyzer (culture-sensitivity) and
// IntegralParseOverflowAnalyzer (overflow) separate despite both being about numeric parsing. Scoped to
// a LITERAL negative integer only, same reasoning as the rest of this family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeVersionComponentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0265";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative Version component always throws ArgumentOutOfRangeException",
        messageFormat: "'new Version(...)' always throws ArgumentOutOfRangeException at runtime — version components cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The Version constructors throw ArgumentOutOfRangeException when major, minor, build, or revision is negative.");

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
        if (creation.ArgumentList is not { Arguments.Count: 2 or 3 or 4 } argumentList) return;
        if (!argumentList.Arguments.Any(a => IsNegativeLiteral(a.Expression))) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Version") return;
        if (!ctor.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static bool IsNegativeLiteral(ExpressionSyntax expression) =>
        expression is PrefixUnaryExpressionSyntax
        {
            RawKind: (int)SyntaxKind.UnaryMinusExpression,
            Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression },
        };
}
