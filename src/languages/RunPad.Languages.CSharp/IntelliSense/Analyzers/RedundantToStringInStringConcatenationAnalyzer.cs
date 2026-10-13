using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// A natural sibling of
// RemoveRedundantToStringCallAnalyzer (RPAD0012): that one catches `.ToString()` on an
// expression whose static type is already string; this one catches `.ToString()` on ANY type when
// it's a direct operand of a string `+` concatenation — the '+' operator already stringifies the
// other operand implicitly, so the explicit call adds nothing there either.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantToStringInStringConcatenationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0050";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant ToString() in string concatenation",
        messageFormat: "Calling ToString() here is redundant — the '+' operator already stringifies this operand implicitly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "String concatenation with '+' already calls ToString() on a non-string operand implicitly.");

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
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" } memberAccess)
            return;
        if (invocation.Parent is not BinaryExpressionSyntax { RawKind: (int)SyntaxKind.AddExpression } binary)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Parameters.Length: 0, Name: "ToString" })
            return;

        // Already the receiver's own job for RemoveRedundantToStringCallAnalyzer (RPAD0012) — don't
        // double-report the same call from two different analyzers.
        var receiverType = context.SemanticModel.GetTypeInfo(memberAccess.Expression, ct).Type;
        if (receiverType is { SpecialType: SpecialType.System_String }) return;

        var otherSide = ReferenceEquals(binary.Left, invocation) ? binary.Right : binary.Left;
        var otherType = context.SemanticModel.GetTypeInfo(otherSide, ct).Type;
        if (otherType is not { SpecialType: SpecialType.System_String }) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
