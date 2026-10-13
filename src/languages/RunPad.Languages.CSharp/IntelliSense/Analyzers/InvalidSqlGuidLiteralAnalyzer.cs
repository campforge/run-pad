using System.Collections.Immutable;
using System.Data.SqlTypes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #9, the LAST candidate from the third research subagent
// survey (411-й проход, full tool access, empirically self-verified), re-verified independently before
// implementing. `System.Data.SqlTypes.SqlGuid(string)` constructor and `SqlGuid.Parse(string)` — the
// agent itself flagged this as its lowest-novelty/weakest candidate, since SqlGuid's accepted format set
// looks functionally identical to `System.Guid`'s own formats (unlike the genuinely-different-value-domain
// SqlMoney/SqlDecimal/SqlDateTime/SqlBoolean siblings already in this catalog, RPAD0388/0395/0396/0409).
// Confirmed empirically: BOTH entry points share a single exception type, `FormatException`, with the
// identical message text ("Unrecognized Guid format.") as `Guid`'s own constructor/`Parse` — strongly
// suggesting SqlGuid delegates to `Guid` internally. Confirmed the same five accepted formats (D/N/B/P/X)
// as `InvalidGuidConstructorLiteralAnalyzer` (RPAD0402, проход 411 of the OVERALL session — note this
// analyzer's own проход number, 421, is unrelated to that numeric coincidence). Still implemented despite
// the low novelty — it's a genuinely distinct, unguarded call shape (a different type than `Guid`), and
// completes the third agent's full candidate list.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSqlGuidLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0412";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SqlGuid argument is not a recognized Guid format",
        messageFormat: "'{0}' always throws FormatException at runtime — '{1}' doesn't match any of SqlGuid's recognized formats (32 hex digits, optionally grouped 8-4-4-4-12 with dashes, optionally wrapped in braces or parentheses, or the hex-array \"X\" format)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The SqlGuid(string) constructor and SqlGuid.Parse(string) both throw FormatException when the argument doesn't match any of Guid's recognized textual formats.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        ArgumentListSyntax? argumentList;
        string callText;
        ISymbol? symbol;
        var ct = context.CancellationToken;

        switch (context.Node)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" } } invocation:
                argumentList = invocation.ArgumentList;
                callText = "SqlGuid.Parse(...)";
                symbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol;
                break;
            case BaseObjectCreationExpressionSyntax creation:
                argumentList = creation.ArgumentList;
                callText = "new SqlGuid(...)";
                symbol = context.SemanticModel.GetSymbolInfo(creation, ct).Symbol;
                break;
            default:
                return;
        }

        if (argumentList is not { Arguments: [{ Expression: var arg }] }) return;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string text) return;

        if (symbol is not IMethodSymbol method) return;
        var isMatch = method switch
        {
            { MethodKind: MethodKind.Ordinary, IsStatic: true, Name: "Parse", Parameters: [{ Type.SpecialType: SpecialType.System_String }] } =>
                method.ContainingType?.ToDisplayString() == "System.Data.SqlTypes.SqlGuid",
            { MethodKind: MethodKind.Constructor, Parameters: [{ Type.SpecialType: SpecialType.System_String }] } =>
                method.ContainingType?.ToDisplayString() == "System.Data.SqlTypes.SqlGuid",
            _ => false,
        };
        if (!isMatch) return;

        try
        {
            _ = new SqlGuid(text);
        }
        catch (FormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), callText, text));
        }
    }
}
