using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `s.Substring(start, length).AsSpan()` (or the one-argument `s.Substring(start).AsSpan()`) allocates
// a brand-new string via Substring just so it can immediately be viewed as a span and (usually)
// discarded — `s.AsSpan(start, length)`/`s.AsSpan(start)` produces the same characters as a
// ReadOnlySpan<char> directly over the original string's memory, without the intermediate allocation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferDirectAsSpanOverSubstringThenAsSpanAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0188";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer AsSpan(start, length) over Substring(...).AsSpan()",
        messageFormat: "'Substring(...).AsSpan()' allocates a new string just to view it as a span — call 'AsSpan(...)' with the same arguments directly on the original string instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Substring(start[, length]).AsSpan() allocates an intermediate string that AsSpan(start[, length]) does not need — it views the same characters over the original string's memory.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax
            {
                Name.Identifier.Text: "AsSpan",
                Expression: InvocationExpressionSyntax substringInvocation,
            })
        {
            return;
        }
        if (invocation.ArgumentList.Arguments.Count != 0) return;
        if (substringInvocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Substring" })
            return;
        if (substringInvocation.ArgumentList.Arguments.Count is not (1 or 2))
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(substringInvocation, ct).Symbol is not IMethodSymbol { Name: "Substring" } substringMethod)
            return;
        if (substringMethod.ContainingType?.SpecialType != SpecialType.System_String) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "AsSpan" } asSpanMethod)
            return;
        if (asSpanMethod.ContainingType?.ToDisplayString() != "System.MemoryExtensions") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
