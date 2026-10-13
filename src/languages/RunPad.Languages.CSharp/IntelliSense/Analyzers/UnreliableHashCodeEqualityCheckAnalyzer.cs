using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `a.GetHashCode() == b.GetHashCode()` looks like a cheap equality check, but GetHashCode makes no
// promise of uniqueness — two unequal objects are allowed to (and eventually will) share a hash code.
// Using it as a stand-in for Equals() is a genuine, if-rare-to-trigger, correctness bug: `a.Equals(b)`
// says what's actually meant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnreliableHashCodeEqualityCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0075";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Unreliable GetHashCode() equality check",
        messageFormat: "Comparing GetHashCode() results is not a reliable equality check — hash collisions are allowed; use Equals() instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "GetHashCode makes no uniqueness guarantee — two unequal values can share a hash code, so comparing hash codes can produce a false 'equal' result.");

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
        if (!IsGetHashCodeCall(context, binary.Left) || !IsGetHashCodeCall(context, binary.Right)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsGetHashCodeCall(SyntaxNodeAnalysisContext context, ExpressionSyntax expression)
    {
        if (expression is not InvocationExpressionSyntax
            {
                ArgumentList.Arguments.Count: 0,
                Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "GetHashCode" },
            } invocation)
        {
            return false;
        }

        return context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol
            is IMethodSymbol { Name: "GetHashCode", Parameters.Length: 0 };
    }
}
