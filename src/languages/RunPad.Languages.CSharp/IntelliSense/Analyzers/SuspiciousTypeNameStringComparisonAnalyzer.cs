using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят четвёртый проход, batch) — own pick, not a Roslynator port.
// `obj.GetType().Name == "Foo"` is a fragile stand-in for a real type check: renaming the type
// silently breaks the string literal (no compile error), and it doesn't account for inheritance the
// way `obj is Foo`/`obj.GetType() == typeof(Foo)` naturally would. Warning, not Info — this is a
// correctness/maintainability risk, not a style nit.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SuspiciousTypeNameStringComparisonAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0087";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Suspicious GetType().Name string comparison",
        messageFormat: "Comparing 'GetType().Name' to a string literal is fragile — a rename silently breaks it and it ignores inheritance; use 'is' or 'typeof(...)' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "GetType().Name == \"literal\" is a string-based type check that a type rename won't update and that treats every subtype as a mismatch.");

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

        var matches = (IsGetTypeNameAccess(context, binary.Left) && IsStringLiteral(binary.Right))
            || (IsGetTypeNameAccess(context, binary.Right) && IsStringLiteral(binary.Left));
        if (!matches) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsGetTypeNameAccess(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "Name",
                Expression: InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } getTypeInvocation,
            } nameAccess)
        {
            return false;
        }
        if (getTypeInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetType" }) return false;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(getTypeInvocation, ct).Symbol is not IMethodSymbol { Name: "GetType", Parameters.Length: 0 })
            return false;
        // Type doesn't override Name — member lookup resolves it straight to the inherited
        // MemberInfo.Name declaration (confirmed empirically, not assumed from reference source).
        return context.SemanticModel.GetSymbolInfo(nameAccess, ct).Symbol is IPropertySymbol { Name: "Name" } nameProperty
            && nameProperty.ContainingType?.ToDisplayString() == "System.Reflection.MemberInfo";
    }

    private static bool IsStringLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression };
}
