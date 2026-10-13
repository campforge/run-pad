using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `x.Equals(x)`/`object.ReferenceEquals(x, x)` always return true — almost certainly a copy-paste
// mistake where one side was meant to be something else. The compiler's own CS1718 already catches
// the operator form (`x == x`/`x != x`), but that check is specific to binary operators and doesn't
// extend to these method-call forms, leaving a real gap. Deliberately restricted to a bare identifier
// on both sides (not general expressions) — comparing the results of two separate method CALLS that
// happen to look identical isn't necessarily comparing the same value if the call has side effects, so
// only the unambiguous "exact same local/parameter/field" case is flagged.
// Extended to also cover `x.CompareTo(x)` — an IComparable
// self-compare always returns 0, same underlying mistake, one more method in the same family.
// Extended to also cover `x.HasFlag(x)` — checking that
// every bit of x is set in x itself is always true, same underlying self-comparison mistake.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SelfEqualityCheckAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0096";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Comparing a value to itself",
        messageFormat: "Comparing a value to itself always returns the same result — likely a copy-paste mistake",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "x.Equals(x), ReferenceEquals(x, x), x.CompareTo(x), and x.HasFlag(x) always return the same result regardless of x's value; this compiles fine but is almost never the intended comparison.");

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
        var ct = context.CancellationToken;

        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "Equals", Expression: IdentifierNameSyntax receiverId }
            && invocation.ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax argId }]
            && receiverId.Identifier.Text == argId.Identifier.Text
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol { Name: "Equals", Parameters.Length: 1 })
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
            return;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "CompareTo", Expression: IdentifierNameSyntax compareReceiverId }
            && invocation.ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax compareArgId }]
            && compareReceiverId.Identifier.Text == compareArgId.Identifier.Text
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol { Name: "CompareTo", Parameters.Length: 1 })
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
            return;
        }

        if (invocation.Expression is MemberAccessExpressionSyntax { Name.Identifier.Text: "HasFlag", Expression: IdentifierNameSyntax hasFlagReceiverId }
            && invocation.ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax hasFlagArgId }]
            && hasFlagReceiverId.Identifier.Text == hasFlagArgId.Identifier.Text
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol { Name: "HasFlag" } hasFlagMethod
            && hasFlagMethod.ContainingType?.ToDisplayString() == "System.Enum")
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
            return;
        }

        var methodNameNode = invocation.Expression switch
        {
            IdentifierNameSyntax id => id,
            MemberAccessExpressionSyntax { Name: IdentifierNameSyntax id } => id,
            _ => null,
        };
        if (methodNameNode?.Identifier.Text == "ReferenceEquals"
            && invocation.ArgumentList.Arguments is [{ Expression: IdentifierNameSyntax firstId }, { Expression: IdentifierNameSyntax secondId }]
            && firstId.Identifier.Text == secondId.Identifier.Text
            && context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is IMethodSymbol
            {
                Name: "ReferenceEquals", ContainingType.SpecialType: SpecialType.System_Object,
            })
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
        }
    }
}
