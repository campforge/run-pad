using System.Collections.Immutable;
using System.Data.SqlTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; found by direct
// brainstorming, same pass that found RPAD0379-RPAD0387 — grep-confirmed no prior coverage.
// Thematically adjacent to, but genuinely NOT overlapping with, InvalidDateOrTimeParseLiteralAnalyzer
// (RPAD0365, проход 372): that analyzer covers DateTime/DateTimeOffset/TimeSpan/DateOnly/TimeOnly.Parse,
// none of which is `System.Data.SqlTypes.SqlDateTime` — a distinct type with its OWN, narrower valid date
// range (year 1753-9999, matching SQL Server's historical `datetime` column type), same class of
// legitimate coexistence already established for ContentType/MediaTypeHeaderValue in this catalog.
// Confirmed empirically this is genuinely a TWO-exception-type situation: a string that isn't parseable as
// a DateTime AT ALL throws `FormatException` (SqlDateTime.Parse delegates to regular DateTime parsing
// first), while a string that parses fine as a DateTime but falls outside SqlDateTime's narrower range
// (e.g. any date before 1753-01-01) throws `System.Data.SqlTypes.SqlTypeException` instead — the message
// format parameterizes the exception type name, same technique this catalog's other multi-exception-type
// analyzers already use.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSqlDateTimeParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0388";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SqlDateTime.Parse argument is not a valid SqlDateTime literal",
        messageFormat: "'SqlDateTime.Parse(...)' always throws {0} at runtime — '{1}' is not a valid SqlDateTime literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SqlDateTime.Parse(string) throws FormatException for a string that isn't a valid DateTime, or SqlTypeException for a valid DateTime outside SqlDateTime's narrower 1753-9999 range.");

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
        if (containingType.ToDisplayString() != "System.Data.SqlTypes.SqlDateTime") return;

        try
        {
            _ = SqlDateTime.Parse(value);
        }
        catch (System.Exception ex) when (ex is System.FormatException or SqlTypeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, value));
        }
    }
}
