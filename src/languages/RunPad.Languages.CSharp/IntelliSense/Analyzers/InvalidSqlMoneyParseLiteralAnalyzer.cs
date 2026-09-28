using System.Collections.Immutable;
using System.Data.SqlTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (четыреста третий проход, batch) — own pick, not a Roslynator port; sixth candidate from the
// second research subagent survey (проход 397, read-only tools) — the agent flagged this one explicitly as
// an unverified "analogy only, zero doc evidence" guess (mirroring the already-shipped SqlDateTime.Parse,
// RPAD0388), so it was re-derived from scratch empirically rather than trusted at all. `SqlMoney.Parse
// (string)` — single overload (confirmed via reflection), grep-confirmed no prior coverage. Confirmed
// empirically this is genuinely a TWO-exception-type situation, same class as SqlDateTime.Parse: unparseable
// text throws `FormatException`, while a numerically well-formed value outside SqlMoney's range (±922,337,
// 203,685,477.5807/5808, matching SQL Server's `money` column type bounds) throws `OverflowException`
// instead — the message format parameterizes the exception type name. Thematically adjacent to, but
// genuinely NOT overlapping with, `decimal.Parse` coverage in InvalidNumericParseFormatAnalyzer (RPAD0356)
// — a different, much narrower numeric range, same class of legitimate coexistence already established for
// ContentType/MediaTypeHeaderValue and DateTime.Parse/SqlDateTime.Parse in this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSqlMoneyParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0395";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SqlMoney.Parse argument is not a valid SqlMoney literal",
        messageFormat: "'SqlMoney.Parse(...)' always throws {0} at runtime — '{1}' is not a valid SqlMoney literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SqlMoney.Parse(string) throws FormatException for text that isn't a valid number, or OverflowException for a value outside SqlMoney's +-922,337,203,685,477.5807 range.");

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
        if (containingType.ToDisplayString() != "System.Data.SqlTypes.SqlMoney") return;

        try
        {
            _ = SqlMoney.Parse(value);
        }
        catch (System.Exception ex) when (ex is System.FormatException or System.OverflowException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, value));
        }
    }
}
