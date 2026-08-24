using System.Collections.Immutable;
using System.Data.SqlTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (445-й проход, batch) — candidate #1 from the ninth research subagent survey, re-verified
// independently before implementing. `SqlByte`/`SqlInt16`/`SqlInt32`/`SqlInt64.Parse(string)` — the four
// integer members of the `System.Data.SqlTypes` family the existing SqlMoney/SqlDecimal/SqlBoolean/
// SqlDateTime/SqlGuid analyzers simply never got around to (grep-confirmed no prior coverage). Confirmed
// empirically, matching the agent's report: a `FormatException` for non-numeric-shaped text (`"abc"`,
// `""`, whitespace-only, a decimal point, a thousands separator, `"0x10"`, a digit-group underscore,
// scientific notation, `"NaN"`, `"∞"`), or an `OverflowException` for a pure digit run too large for the
// target type. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP/fr-FR for every malformed literal this
// analyzer actually uses. The agent separately flagged that `SqlInt32.Parse("+5")`/`"-5"` diverge under
// ar-SA specifically (a genuine culture-sensitivity the agent found, unlike plain `int.Parse`) — noted here
// but not a concern for THIS analyzer's scope, since none of the malformed shapes it flags are sign-prefix
// dependent; a stray leading `+`/`-` on an otherwise-malformed literal is simply not a case this analyzer
// reasons about either way. `SqlDouble.Parse`/`SqlSingle.Parse` were investigated by the agent and found
// genuinely culture-sensitive without a clean invariant boundary (thousands-separator/decimal-point
// interpretation itself shifts by culture) — deliberately NOT extended to those two floating-point
// siblings.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSqlIntegerParseLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0434";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SqlByte/SqlInt16/SqlInt32/SqlInt64.Parse argument is not a valid integer literal",
        messageFormat: "'{0}.Parse(...)' always throws {1} at runtime — '{2}' is not a valid {0} literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SqlByte/SqlInt16/SqlInt32/SqlInt64.Parse(string) throw FormatException for text that isn't a valid plain integer, or OverflowException for a value out of the target type's range.");

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
        var typeName = containingType.ToDisplayString();
        var shortName = typeName switch
        {
            "System.Data.SqlTypes.SqlByte" => "SqlByte",
            "System.Data.SqlTypes.SqlInt16" => "SqlInt16",
            "System.Data.SqlTypes.SqlInt32" => "SqlInt32",
            "System.Data.SqlTypes.SqlInt64" => "SqlInt64",
            _ => null,
        };
        if (shortName is null) return;

        System.Exception? thrown = null;
        try
        {
            _ = shortName switch
            {
                "SqlByte" => (object)SqlByte.Parse(value),
                "SqlInt16" => SqlInt16.Parse(value),
                "SqlInt32" => SqlInt32.Parse(value),
                "SqlInt64" => SqlInt64.Parse(value),
                _ => null!,
            };
        }
        catch (System.Exception ex) when (ex is System.FormatException or System.OverflowException)
        {
            thrown = ex;
        }
        if (thrown is null) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), shortName, thrown.GetType().Name, value));
    }
}
