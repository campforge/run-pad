using System.Collections.Immutable;
using System.Data.SqlTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; sibling of RPAD0395's
// SqlMoney.Parse, same "analogy only, zero doc evidence" flag from the second research subagent survey
// (проход 397), re-derived empirically before implementing. `SqlDecimal.Parse(string)` — single overload
// (confirmed via reflection), grep-confirmed no prior coverage. Confirmed empirically this is a
// TWO-exception-type situation, but with a genuinely subtler boundary than SqlMoney's: `SqlDecimal` allows
// up to exactly 38 total significant digits (integer + fractional digits combined, sign doesn't count) —
// confirmed empirically that 38 digits succeeds in EITHER shape (pure integer or with a decimal point),
// while 39 digits fails in BOTH shapes, but with a DIFFERENT exception type depending on shape: a 39-digit
// PURE INTEGER throws `OverflowException`, while a 39-digit value WITH a decimal point throws
// `FormatException` instead (same as plain unparseable text) — confirmed empirically rather than assumed,
// since a naive guess would expect both over-precision cases to throw the same way. Thematically adjacent
// to, but genuinely NOT overlapping with, `decimal.Parse` coverage in InvalidNumericParseFormatAnalyzer
// (RPAD0356) — a different maximum precision (38 vs 29 significant digits) and different exception
// boundary shape, same class of legitimate coexistence already established for SqlMoney.Parse/SqlDateTime.
// Parse in this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSqlDecimalParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0396";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SqlDecimal.Parse argument is not a valid SqlDecimal literal",
        messageFormat: "'SqlDecimal.Parse(...)' always throws {0} at runtime — '{1}' is not a valid SqlDecimal literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SqlDecimal.Parse(string) throws FormatException for text that isn't a valid number (or has more than 38 significant digits with a decimal point), or OverflowException for a pure integer with more than 38 significant digits.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Parse",
                IsStatic: true,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Data.SqlTypes.SqlDecimal") return;

        try
        {
            _ = SqlDecimal.Parse(value);
        }
        catch (System.Exception ex) when (ex is System.FormatException or System.OverflowException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, value));
        }
    }
}
