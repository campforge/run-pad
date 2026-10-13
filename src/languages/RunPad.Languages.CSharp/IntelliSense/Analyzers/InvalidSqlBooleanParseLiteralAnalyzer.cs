using System.Collections.Immutable;
using System.Data.SqlTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #6 from the third research subagent survey (411-й проход,
// full tool access, empirically self-verified), re-verified independently before implementing.
// `SqlBoolean.Parse(string)` — joins the SqlMoney/SqlDecimal/SqlDateTime family already in this catalog
// (RPAD0388/0395/0396), a genuinely different value domain from `bool.Parse` (accepts "1"/"0" and any
// other numeric string as well as "true"/"false" case-insensitively, per SQL Server's bit-to-boolean
// convention). Confirmed empirically, matching the agent's report exactly: a NOTABLY surprising two-
// exception-type split — an empty string OR a whitespace-only string throws `IndexOutOfRangeException`
// (not `FormatException` — the implementation indexes into the trimmed string before checking its length),
// while any other unrecognized text throws `FormatException`. Confirmed "2", "3", etc. (any parseable
// integer, not just 0/1) are accepted as truthy/falsy without throwing, matching SQL Server's `bit`
// semantics of "any nonzero integer is true".
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSqlBooleanParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0409";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SqlBoolean.Parse argument is not a recognized boolean representation",
        messageFormat: "'SqlBoolean.Parse(...)' always throws {0} at runtime — '{1}' is not a recognized SqlBoolean representation",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SqlBoolean.Parse(string) throws IndexOutOfRangeException for an empty or whitespace-only string, or FormatException for other unrecognized text.");

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
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

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
        if (containingType.ToDisplayString() != "System.Data.SqlTypes.SqlBoolean") return;

        try
        {
            SqlBoolean.Parse(text);
        }
        catch (System.Exception ex) when (ex is IndexOutOfRangeException or FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.GetType().Name, text));
        }
    }
}
