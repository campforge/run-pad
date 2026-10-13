using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `xs.Take(n).First()` (or FirstOrDefault) truncates the sequence to at most n elements just to grab
// the first one — First()/FirstOrDefault() only ever look at the first element regardless of how many
// followed it, so the Take is inert. Deliberately restricted to a LITERAL n >= 1: for n == 0,
// Take(0).First() always throws while xs.First() only throws when xs is empty — genuinely different
// behavior — and a non-literal n could evaluate to 0 at runtime, so only a literal that's provably >= 1
// is safe to flag. NOT extended to Single/SingleOrDefault — Take artificially caps the element count,
// which changes whether Single considers the sequence to have "too many" elements, so Take(n).Single()
// and xs.Single() are NOT equivalent in general.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantTakeBeforeFirstAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0071";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant Take before First",
        messageFormat: "'Take(...).{0}()' can be simplified to '{0}()' — Take doesn't change which element is first",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Take(n) followed immediately by First()/FirstOrDefault() is redundant for any n >= 1 — First only ever looks at the first element.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TerminalMethodNames = ImmutableHashSet.Create("First", "FirstOrDefault");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var terminalName } memberAccess)
            return;
        if (!TerminalMethodNames.Contains(terminalName)) return;

        var ct = context.CancellationToken;
        if (!IsRealEnumerableMethod(context, invocation, ct)) return;

        if (memberAccess.Expression is not InvocationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var takeArgExpr }] } takeInvocation)
            return;
        if (takeInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Take" }) return;
        if (!IsPositiveLiteral(takeArgExpr)) return;
        if (!IsRealEnumerableMethod(context, takeInvocation, ct)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), terminalName));
    }

    private static bool IsPositiveLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
        && int.TryParse(literal.Token.ValueText, out var value) && value >= 1;

    private static bool IsRealEnumerableMethod(SyntaxNodeAnalysisContext context, InvocationExpressionSyntax invocation, CancellationToken ct)
    {
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return false;
        var original = method.ReducedFrom ?? method.OriginalDefinition;
        return original.ContainingType?.ToDisplayString() == "System.Linq.Enumerable";
    }
}
