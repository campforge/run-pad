using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести шестьдесят пятый проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as the Parse-family analyzers, but on a numeric (not string)
// constant: `TimeSpan.FromDays(double)`/`FromHours`/`FromMinutes`/`FromSeconds`/`FromMilliseconds` throw
// `ArgumentException` for `double.NaN` and `OverflowException` for a value whose equivalent time span
// doesn't fit TimeSpan's own range (confirmed empirically consistent across all five methods, including
// ±Infinity and large-but-finite values, before writing this) — a mistake the compiler can't catch since
// the argument is a perfectly normal `double`. Rather than hand-deriving each method's exact overflow
// threshold, this analyzer calls the REAL `TimeSpan.FromXxx` in-process on the literal's own constant
// value (obtained via the semantic model, so this works for `double.NaN`/`double.PositiveInfinity`
// too, not just numeric literals) inside a try/catch, and reports only if THAT call itself throws — a
// numeric constant has no culture dependency to worry about (unlike the string-literal Parse analyzers),
// since GetConstantValue already resolves it to the exact double value the runtime call would receive.
// Extended in the триста шестнадцатый проход to also cover `TimeSpan.FromMicroseconds(double)` (added in
// .NET 7, missed in the original батч) — confirmed empirically to throw identically to the other five.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTimeSpanFromDoubleArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0259";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TimeSpan.FromXxx argument always throws",
        messageFormat: "'TimeSpan.{0}({1})' always throws at runtime — the value is NaN or out of TimeSpan's representable range",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TimeSpan.FromDays/FromHours/FromMinutes/FromSeconds/FromMilliseconds/FromMicroseconds throw ArgumentException for NaN and OverflowException for a value outside TimeSpan's representable range.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FromDays" or "FromHours" or "FromMinutes" or "FromSeconds" or "FromMilliseconds" or "FromMicroseconds" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not double value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.TimeSpan") return;

        if (AlwaysThrows(methodName, value))
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, value));
        }
    }

    private static bool AlwaysThrows(string methodName, double value)
    {
        try
        {
            _ = methodName switch
            {
                "FromDays" => TimeSpan.FromDays(value),
                "FromHours" => TimeSpan.FromHours(value),
                "FromMinutes" => TimeSpan.FromMinutes(value),
                "FromSeconds" => TimeSpan.FromSeconds(value),
                "FromMilliseconds" => TimeSpan.FromMilliseconds(value),
                "FromMicroseconds" => TimeSpan.FromMicroseconds(value),
                _ => TimeSpan.Zero,
            };
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or OverflowException)
        {
            return true;
        }
    }
}
