using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `DateTime.Compare(a, b) == 0` (and the other comparison outcomes) says the same thing the relational
// operators already say directly — DateTime overloads `==`/`!=`/`<`/`>`/`<=`/`>=`, so there's no need
// to go through the three-way Compare and then test its sign.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEqualityOperatorOverDateTimeCompareAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0173";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer a relational operator over DateTime.Compare",
        messageFormat: "'DateTime.Compare(a, b) {0} 0' can be simplified to 'a {1} b' — DateTime already supports the relational operators directly",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "DateTime.Compare(a, b) compared against zero says the same thing the relational operators (==, !=, <, >, <=, >=) already say directly on two DateTime values.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.EqualsExpression, SyntaxKind.NotEqualsExpression,
            SyntaxKind.LessThanExpression, SyntaxKind.GreaterThanExpression,
            SyntaxKind.LessThanOrEqualExpression, SyntaxKind.GreaterThanOrEqualExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        InvocationExpressionSyntax? compareInvocation = binary.Left switch
        {
            InvocationExpressionSyntax call when IsZeroLiteral(binary.Right) => call,
            _ => null,
        };
        var operatorText = OperatorFor(binary.Kind());
        if (compareInvocation is null || operatorText is null) return;

        if (compareInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Compare" }) return;
        if (compareInvocation.ArgumentList.Arguments.Count != 2) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(compareInvocation, ct).Symbol is not IMethodSymbol { Name: "Compare", IsStatic: true } compareMethod)
            return;
        if (compareMethod.ContainingType?.ToDisplayString() != "System.DateTime") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation(), operatorText, operatorText));
    }

    private static bool IsZeroLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { Token.Value: 0 };

    private static string? OperatorFor(SyntaxKind kind) => kind switch
    {
        SyntaxKind.EqualsExpression => "==",
        SyntaxKind.NotEqualsExpression => "!=",
        SyntaxKind.LessThanExpression => "<",
        SyntaxKind.GreaterThanExpression => ">",
        SyntaxKind.LessThanOrEqualExpression => "<=",
        SyntaxKind.GreaterThanOrEqualExpression => ">=",
        _ => null,
    };
}
