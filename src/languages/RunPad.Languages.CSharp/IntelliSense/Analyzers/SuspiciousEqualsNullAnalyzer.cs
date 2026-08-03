using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят второй проход, batch) — own pick, not a Roslynator port. `x.Equals(null)`
// looks like a safe way to check for null, but it's an INSTANCE method call — if x itself is actually
// null, the call throws NullReferenceException instead of returning a sensible bool. `x is null`/
// `x == null` check for null without ever dereferencing x. Scoped to reference types only — a struct
// receiver can never be null (Nullable<T>.Equals(null) is the normal, safe way to test HasValue, and
// IsValueType excludes it here).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SuspiciousEqualsNullAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0074";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Suspicious Equals(null) null check",
        messageFormat: "'.Equals(null)' throws NullReferenceException if the receiver is itself null — use 'is null' or '== null' instead",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Equals(null) is an instance call — unlike is null/== null, it dereferences the receiver, so it throws instead of returning false when the receiver is null.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Equals", Expression: var receiver })
            return;
        if (receiver is ThisExpressionSyntax or BaseExpressionSyntax) return; // never null in an instance context
        if (invocation.ArgumentList.Arguments is not [{ Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NullLiteralExpression } }])
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Equals", Parameters.Length: 1 })
            return;

        var receiverType = context.SemanticModel.GetTypeInfo(receiver, ct).Type;
        if (receiverType is not { IsValueType: false }) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
