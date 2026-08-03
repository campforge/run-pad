using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести двадцать девятый проход, batch) — own pick, not a Roslynator port.
// DateTime/DateTimeOffset/TimeSpan all expose an instance `Add(TimeSpan)`/`Subtract(TimeSpan)` (and,
// for DateTime/DateTimeOffset, `Subtract(DateTime)`/`Subtract(DateTimeOffset)` returning a TimeSpan) —
// every one of these has a matching operator overload (`+`/`-`) that does exactly the same arithmetic
// without the explicit method call. Scoped to exactly these three types and exactly these two method
// names (neither type has any OTHER method literally named "Add"/"Subtract" — the day/hour/etc. helpers
// are separately named AddDays/AddHours/…, so there's no ambiguity to resolve).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferOperatorOverAddSubtractMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0217";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer the +/- operator over Add/Subtract on DateTime, DateTimeOffset or TimeSpan",
        messageFormat: "'x.{0}(y)' can be replaced with 'x {1} y' — the operator does the same arithmetic without an explicit method call",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "DateTime/DateTimeOffset/TimeSpan's Add(...)/Subtract(...) methods have matching +/- operator overloads that do the same arithmetic more directly.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetTypes = ImmutableHashSet.Create(
        "System.DateTime", "System.DateTimeOffset", "System.TimeSpan");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" or "Subtract" })
            return;
        if (invocation.ArgumentList.Arguments.Count != 1) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var methodName } method)
            return;
        if (method.ContainingType is not { } containingType || !TargetTypes.Contains(containingType.ToDisplayString()))
            return;

        var operatorText = methodName == "Add" ? "+" : "-";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, operatorText));
    }
}
