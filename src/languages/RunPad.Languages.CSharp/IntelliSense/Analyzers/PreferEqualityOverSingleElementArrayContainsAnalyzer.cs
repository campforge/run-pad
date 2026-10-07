using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто семьдесят девятый проход, batch) — own pick, not a Roslynator port.
// `new[] { x }.Contains(y)` builds a throwaway one-element array just to ask whether `y` equals its
// only element — `x == y` (or `x.Equals(y)` for non-primitive types where `==` isn't overloaded) asks
// the exact same question directly, without allocating an array or calling into LINQ's Contains.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferEqualityOverSingleElementArrayContainsAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0155";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant single-element array before Contains",
        messageFormat: "A single-element array literal used as the Contains receiver allocates an array just to compare one element — use a direct equality check instead",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A single-element array literal used only as a Contains receiver allocates an array to ask a question a direct equality check already answers.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Contains" } containsAccess)
            return;
        if (invocation.ArgumentList.Arguments.Count != 1) return;

        var hasSingleElement = containsAccess.Expression switch
        {
            ArrayCreationExpressionSyntax { Initializer.Expressions.Count: 1 } => true,
            ImplicitArrayCreationExpressionSyntax { Initializer.Expressions.Count: 1 } => true,
            _ => false,
        };
        if (!hasSingleElement) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Contains" } method)
            return;
        // C# 14 first-class spans: with `using System` in scope (RunPad's implicit usings) an array
        // receiver binds to MemoryExtensions.Contains(ReadOnlySpan<T>, T) rather than
        // Enumerable.Contains — the same array allocation, the same fix.
        if (method.ContainingType?.ToDisplayString() is not ("System.Linq.Enumerable" or "System.MemoryExtensions"))
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
