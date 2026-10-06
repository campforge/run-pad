using System.Collections.Immutable;
using System.Net;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (445-й проход, batch) — candidate #2 from the ninth research subagent survey, re-verified
// independently before implementing. `System.Net.WebProxy(string address)` — a distinct type from the
// already-covered `Uri`/`UriBuilder(string)`, grep-confirmed no prior coverage. Confirmed empirically:
// throws `UriFormatException` eagerly in the constructor itself (no lazy evaluation, no side effects —
// same shape already established for `Uri`/`UriBuilder`) for an empty string, text containing spaces, a
// missing scheme (`"://bad"`), a malformed scheme character (`"ht!tp://x"`), or a scheme with no host
// (`"http://"`). Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. Confirmed leniency worth noting for
// correct test literals: a bare hostname with no scheme at all (e.g. `"hello"`) is accepted (implicitly
// treated as `http://hello/`, the same leniency `UriBuilder(string)` already established) — this analyzer
// never flags that shape, since it simply runs the real constructor rather than hand-rolling a scheme
// check.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidWebProxyLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0435";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "WebProxy address argument is not a well-formed URI",
        messageFormat: "'new WebProxy(...)' always throws UriFormatException at runtime — '{0}' is not a well-formed URI",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The WebProxy(string address) constructor throws UriFormatException for text that isn't a well-formed URI.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var addressArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(addressArg, ct).Value is not string address) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
            } method)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Net.WebProxy") return;

        try
        {
            _ = new WebProxy(address);
        }
        catch (UriFormatException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), address));
        }
    }
}
