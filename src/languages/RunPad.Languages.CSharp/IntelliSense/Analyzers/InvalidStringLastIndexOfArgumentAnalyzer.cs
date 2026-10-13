using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. The
// backward-searching counterpart to InvalidStringIndexOfCharArgumentAnalyzer (RPAD0299),
// kept as a SEPARATE analyzer for the same reason InvalidArrayLastIndexOfArgumentAnalyzer (RPAD0298) is
// separate from InvalidArrayFillArgumentAnalyzer (RPAD0294): the boundary formula is genuinely different,
// not just a different method name. For `"literal".LastIndexOf(value, startIndex[, count][,
// StringComparison])` (both `char`- and `string`-valued overloads), the valid range is `0 <= startIndex <
// receiver.Length` (STRICTLY less — `startIndex == receiver.Length` always throws here, unlike the
// forward-searching family's valid empty-tail case) AND `count >= 0` AND `startIndex - count + 1 >= 0` —
// confirmed empirically via scratch `dotnet run` script, not assumed. Deliberately SKIPS an empty-string
// receiver entirely (`receiverValue.Length == 0` bails out before checking anything) — the same empirical
// check found the identical BCL leniency already documented for `Array.LastIndexOf` in RPAD0298 (accepts
// both `startIndex == -1` and `startIndex == 0` with `count == 0`/default on an empty receiver). Reuses
// RPAD0299's generalized parameter-shape inspection (position 0 is `char`/`string`, position 1 must be
// `int`, an optional `int` at position 2 is `count`, a trailing `StringComparison` is recognized and
// ignored) — duplicated rather than shared, same small-helper-duplication precedent as the rest of this
// catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringLastIndexOfArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0300";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "LastIndexOf startIndex/count arguments are out of range for a literal receiver",
        messageFormat: "'\"{0}\".LastIndexOf(...)' always throws ArgumentOutOfRangeException at runtime — the receiver has only {1} character(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.LastIndexOf(char/string value, int startIndex[, int count][, StringComparison]) throws ArgumentOutOfRangeException when startIndex is not within the receiver, count is negative, or startIndex - count + 1 is negative.");

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
                Name.Identifier.Text: "LastIndexOf",
                Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } receiverLiteral,
            })
        {
            return;
        }
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var startArg }, ..] arguments)
            return;
        if (arguments.Count is not (>= 2 and <= 4)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(receiverLiteral, ct).Value is not string receiverValue) return;
        if (receiverValue.Length == 0) return;
        if (context.SemanticModel.GetConstantValue(startArg, ct).Value is not int startIndex) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "LastIndexOf" } method) return;
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;
        var parameters = method.Parameters;
        if (parameters.Length != arguments.Count) return;
        if (parameters[0].Type.SpecialType is not (SpecialType.System_Char or SpecialType.System_String)) return;
        if (parameters[1].Type.SpecialType != SpecialType.System_Int32) return;

        int? count = null;
        if (parameters.Length >= 3 && parameters[2].Type.SpecialType == SpecialType.System_Int32)
        {
            if (context.SemanticModel.GetConstantValue(arguments[2].Expression, ct).Value is not int countValue) return;
            count = countValue;
            if (parameters.Length == 4 && parameters[3].Type is not { Name: "StringComparison" }) return;
        }
        else if (parameters.Length == 3 && parameters[2].Type is not { Name: "StringComparison" })
        {
            return;
        }
        else if (parameters.Length == 4)
        {
            return;
        }

        var length = receiverValue.Length;
        var valid = startIndex >= 0 && startIndex < length
            && (count is null || (count >= 0 && startIndex - count + 1 >= 0));
        if (valid) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), receiverValue, length));
    }
}
