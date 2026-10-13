using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `TimeSpan.FromSeconds(0)` (or FromDays/FromHours/FromMinutes/FromMilliseconds/FromTicks with a zero
// argument) constructs a zero-length TimeSpan the long way — `TimeSpan.Zero` already is that value,
// with no unit ambiguity for the reader to resolve. Deliberately doesn't touch `new TimeSpan(0)` — that
// shape already gets a built-in "'new' expression can be simplified" (target-typed new) suggestion on
// the same span in a target-typed context, so a second diagnostic there would just be noise.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferTimeSpanZeroOverFromXWithZeroAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0203";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer TimeSpan.Zero over TimeSpan.FromX(0)",
        messageFormat: "'TimeSpan.{0}(0)' can be replaced with 'TimeSpan.Zero' — it's the same zero-length value without a unit to read past",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "TimeSpan.FromSeconds(0)/FromMinutes(0)/etc. all construct the same zero-length TimeSpan that TimeSpan.Zero already is.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> ZeroArgMethods = ImmutableHashSet.Create(
        "FromDays", "FromHours", "FromMinutes", "FromSeconds", "FromMilliseconds", "FromTicks");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!ZeroArgMethods.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(arg, ct);
        if (constant.Value is not (0 or 0.0 or 0L)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.TimeSpan") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
