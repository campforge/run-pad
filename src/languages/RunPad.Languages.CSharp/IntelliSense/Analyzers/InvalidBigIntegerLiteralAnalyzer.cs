using System.Collections.Immutable;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the
// real validator" theme as InvalidBase64LiteralLengthAnalyzer (RPAD0245): `BigInteger.Parse(string)`
// throws FormatException for a string that isn't a valid integer literal (empty, whitespace-only,
// containing a decimal point, or any non-digit character) — confirmed empirically via scratch
// `dotnet run` script. Not a duplicate of `IntegralParseOverflowAnalyzer` (RPAD0258) — that analyzer
// covers the FIXED-width integral types (`byte`/`short`/`int`/`long`/etc.), which `BigInteger`
// (arbitrary precision, no upper/lower bound) is not among; its failure mode is purely a format
// problem, never an overflow.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBigIntegerLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0348";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BigInteger.Parse argument is not a valid integer literal",
        messageFormat: "'BigInteger.Parse(...)' always throws FormatException at runtime — '{0}' is not a valid integer literal",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BigInteger.Parse(string) throws FormatException for a string that isn't a valid integer literal.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Numerics.BigInteger") return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_String }]) return;

        if (!AlwaysThrows(value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), value));
    }

    private static bool AlwaysThrows(string value)
    {
        try
        {
            _ = BigInteger.Parse(value);
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }
}
