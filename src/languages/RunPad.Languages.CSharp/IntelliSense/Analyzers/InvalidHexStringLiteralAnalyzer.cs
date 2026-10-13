using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidBase64LiteralLengthAnalyzer (RPAD0245) — the decode-direction
// sibling of `InvalidConvertToBase64StringArgumentAnalyzer`'s `ToHexString`/`ToHexStringLower`
// coverage (RPAD0272, the opposite direction — encode, not decode): `Convert.FromHexString(string)`
// throws FormatException for a string that isn't valid hex — either an odd length, or containing a
// non-hex-digit character — confirmed empirically via scratch `dotnet run` script that both
// conditions produce the same exception type/message shape, so calling the REAL method in a try/catch
// on the literal (rather than hand-deriving the two rules) correctly catches both at once, same
// reasoning as RPAD0245's own rewrite to this technique.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidHexStringLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0345";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Hex string literal is not valid hex",
        messageFormat: "'Convert.FromHexString(...)' always throws FormatException at runtime — '{0}' is not a valid hex string",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.FromHexString throws FormatException for a string that isn't valid hex (odd length, or containing a non-hex-digit character).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "FromHexString" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "FromHexString" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;

        if (AlwaysThrows(value) is not true) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }

    private static bool AlwaysThrows(string value)
    {
        try
        {
            _ = Convert.FromHexString(value);
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }
}
