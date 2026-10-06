using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести четырнадцатый проход, batch) — own pick, not a Roslynator port. A sibling shape
// to PreferDirectEnumComparisonOverToStringAnalyzer (RPAD0141, проход 176 — that one is `enumValue.
// ToString() == "Foo"`, comparing a formatted enum against a string literal). This is `a.ToString() ==
// b.ToString()` where both sides are the SAME enum type — formatting both values to strings just to
// compare the strings, instead of comparing the enum values directly. Slower (two ToString() calls
// thrown away), and fragile the same way RPAD0141 is: a [Flags] enum's ToString() can format as a
// comma-joined list whose member order isn't guaranteed to match between equal underlying values in
// every edge case, and any difference in the two operands' declared enum type is masked once both
// sides are strings.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDirectEnumComparisonOverToStringToStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0200";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer direct enum comparison over comparing ToString() results",
        messageFormat: "'a.ToString() {0} b.ToString()' can be replaced with 'a {0} b' — comparing the enum values directly avoids formatting both to strings first",
        category: "Reliability",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Comparing two enum values by formatting each with ToString() first is slower than comparing the values directly, and can behave unexpectedly for [Flags] enums.");

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
        if (!TryGetToStringReceiver(binary.Left, out var leftReceiver)) return;
        if (!TryGetToStringReceiver(binary.Right, out var rightReceiver)) return;

        var ct = context.CancellationToken;
        var leftType = context.SemanticModel.GetTypeInfo(leftReceiver, ct).Type;
        if (leftType is not { TypeKind: TypeKind.Enum }) return;

        var rightType = context.SemanticModel.GetTypeInfo(rightReceiver, ct).Type;
        if (!SymbolEqualityComparer.Default.Equals(leftType, rightType)) return;

        var operatorText = binary.IsKind(SyntaxKind.EqualsExpression) ? "==" : "!=";
        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operatorText));
    }

    private static bool TryGetToStringReceiver(ExpressionSyntax expression, out ExpressionSyntax receiver)
    {
        receiver = null!;
        if (expression is not InvocationExpressionSyntax
            {
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString", Expression: var candidateReceiver },
                ArgumentList.Arguments.Count: 0,
            })
        {
            return false;
        }
        receiver = candidateReceiver;
        return true;
    }
}
