using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Deliberately
// targets ToLowerInvariant()/ToUpperInvariant() (NOT the culture-sensitive ToLower()/ToUpper(), which
// CultureSensitiveCaseConversionAnalyzer (RPAD0039, this project) already flags on its own — probed
// empirically, and using the culture-sensitive form here would double-message on every occurrence).
// `a.ToLowerInvariant() == b.ToLowerInvariant()` allocates two new strings just to compare them
// case-insensitively — `string.Equals(a, b, StringComparison.OrdinalIgnoreCase)` does the same
// comparison without allocating either one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferOrdinalIgnoreCaseOverInvariantCaseComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0139";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer StringComparison.OrdinalIgnoreCase over comparing invariant-cased strings",
        messageFormat: "Comparing '{0}()' results with '==' allocates two new strings — use '{1}' instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Converting both sides to the same case before comparing them allocates two new strings — string.Equals with StringComparison.OrdinalIgnoreCase compares case-insensitively without allocating either one.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var ct = context.CancellationToken;

        if (!TryGetInvariantCaseConversionMethodName(context.SemanticModel, binary.Left, out var leftMethodName, ct))
            return;
        if (!TryGetInvariantCaseConversionMethodName(context.SemanticModel, binary.Right, out var rightMethodName, ct))
            return;
        if (leftMethodName != rightMethodName) return;

        var suggestion = binary.IsKind(SyntaxKind.NotEqualsExpression)
            ? "!string.Equals(a, b, StringComparison.OrdinalIgnoreCase)"
            : "string.Equals(a, b, StringComparison.OrdinalIgnoreCase)";

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), leftMethodName, suggestion));
    }

    private static bool TryGetInvariantCaseConversionMethodName(
        SemanticModel model, ExpressionSyntax expression, out string? methodName, CancellationToken ct)
    {
        methodName = null;
        if (expression is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ToLowerInvariant" or "ToUpperInvariant" } memberAccess,
            } invocation)
        {
            return false;
        }

        if (model.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { IsStatic: false } method) return false;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return false;

        methodName = memberAccess.Name.Identifier.Text;
        return true;
    }
}
