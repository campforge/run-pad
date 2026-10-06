using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста двадцать первый проход, batch) — own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer/NegativeCountArgumentAnalyzer/
// NegativeTimeoutArgumentAnalyzer/InvalidRandomRangeArgumentAnalyzer (RPAD0231-0234): `string.PadLeft
// (totalWidth[, paddingChar])`/`PadRight(totalWidth[, paddingChar])` throw ArgumentOutOfRangeException
// when `totalWidth` is negative — confirmed empirically via scratch `dotnet run` script, not assumed.
// Unlike the "literal receiver, known length" family (RPAD0235/0281/etc.), this needs no knowledge of the
// receiver string's own length or even a literal receiver at all — negativity of `totalWidth` alone
// determines the throw regardless of what string it's called on. Scoped to a LITERAL negative integer
// only, same reasoning as the rest of this family.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativePadWidthArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0292";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative PadLeft/PadRight totalWidth always throws",
        messageFormat: "'{0}({1})' always throws ArgumentOutOfRangeException at runtime — totalWidth cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.PadLeft/PadRight throw ArgumentOutOfRangeException when totalWidth is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "PadLeft" or "PadRight" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var widthArg }, ..]) return;
        if (!TryGetNegativeIntValue(widthArg, out var width)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, width));
    }

    private static bool TryGetNegativeIntValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is not PrefixUnaryExpressionSyntax
            {
                RawKind: (int)SyntaxKind.UnaryMinusExpression,
                Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal,
            })
        {
            return false;
        }
        if (literal.Token.Value is not int positiveValue) return false;

        value = -positiveValue;
        return true;
    }
}
