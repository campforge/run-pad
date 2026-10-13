using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the real
// validator" theme as InvalidCultureNameLiteralAnalyzer/InvalidIPAddressLiteralAnalyzer/
// InvalidMailAddressLiteralAnalyzer (RPAD0248-0250, проходы 255-257): `bool.Parse(s)` and
// `Convert.ToBoolean(s)` (the string overload of which just calls `bool.Parse` internally) both throw
// FormatException for any string other than "True"/"False", case-insensitively, with optional
// surrounding whitespace — rather than hand-writing that comparison, this analyzer calls the REAL
// `bool.TryParse` in-process on the literal and reports only if THAT call itself returns false. Unlike
// numeric or date parsing, boolean parsing is culture-invariant with no external, environment-dependent
// data table behind it, so there's no risk of the analysis machine and the deployment target disagreeing
// on what's valid.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBooleanLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0253";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "bool.Parse/Convert.ToBoolean argument is not a valid boolean literal",
        messageFormat: "'{0}(...)' always throws FormatException at runtime — '{1}' is not \"True\" or \"False\" (case-insensitive)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "bool.Parse and Convert.ToBoolean throw FormatException for a string other than \"True\"/\"False\", case-insensitively.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" or "ToBoolean" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }

        var containingType = method.ContainingType;
        var isValidTarget = methodName == "Parse"
            ? containingType?.SpecialType == SpecialType.System_Boolean
            : containingType?.ToDisplayString() == "System.Convert";
        if (!isValidTarget) return;

        if (bool.TryParse(value, out _)) return;

        var displayName = methodName == "Parse" ? "bool.Parse" : "Convert.ToBoolean";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), displayName, value));
    }
}
