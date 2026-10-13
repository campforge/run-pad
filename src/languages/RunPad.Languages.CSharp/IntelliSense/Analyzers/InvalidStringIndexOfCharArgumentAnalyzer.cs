using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as InvalidStringRemoveOrInsertArgumentAnalyzer/SubstringOutOfRangeAnalyzer
// (RPAD0281/RPAD0235), applied to a method neither of those cover: `"literal".IndexOf(char value,
// int startIndex[, int count])` throws ArgumentOutOfRangeException when `startIndex`/`count` put the
// search range outside the literal's own known length — confirmed empirically via scratch `dotnet run`
// script, not assumed, including the 2-argument overload's own boundary (`startIndex == length` is valid
// there — an empty search range at the very end — while `startIndex > length` throws). Scoped narrowly to
// the `char`-value overloads only (`IndexOf(char, int)` and `IndexOf(char, int, int)`), matched by the
// RESOLVED method symbol's parameter types rather than just argument count/position — this is what
// correctly excludes the `string`-value 3-argument overload `IndexOf(string, int, StringComparison)`,
// which shares an argument count with `IndexOf(char, int, int)` but not a parameter type in the third
// position. The backward-searching `LastIndexOf` family is deliberately left as a future extension — same
// "MVP first" scoping precedent as elsewhere in this catalog, and the same forward-vs-backward formula
// split already established by InvalidArrayFillArgumentAnalyzer/InvalidArrayLastIndexOfArgumentAnalyzer
// (RPAD0294/RPAD0298).
// Extended in the триста тридцать третий проход to also cover the `string`-value overloads —
// `IndexOf(string, int)`, `IndexOf(string, int, int)`, `IndexOf(string, int, StringComparison)`, and
// `IndexOf(string, int, int, StringComparison)` — confirmed empirically to follow the IDENTICAL boundary
// formula as the `char`-value overloads (a trailing `StringComparison` parameter doesn't change the
// startIndex/count validation at all). Generalized the shape check from a fixed 2-/3-parameter pattern to
// inspecting the RESOLVED parameters generically: position 0 is `char` or `string` (ignored either way,
// only its PRESENCE matters), position 1 must be `int` (`startIndex`), and an optional `int` at position 2
// is `count` — a trailing `StringComparison` (at position 2 or 3) is recognized and ignored, never treated
// as `count`. Class name kept as-is despite the scope growing beyond `char`, same precedent as other
// same-mechanism extensions in this catalog.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringIndexOfCharArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0299";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IndexOf startIndex/count arguments are out of range for a literal receiver",
        messageFormat: "'\"{0}\".IndexOf(...)' always throws ArgumentOutOfRangeException at runtime — the receiver has only {1} character(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.IndexOf(char value, int startIndex[, int count]) throws ArgumentOutOfRangeException when startIndex or count is negative, startIndex exceeds the string's length, or startIndex + count exceeds the string's length.");

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
                Name.Identifier.Text: "IndexOf",
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
        if (context.SemanticModel.GetConstantValue(startArg, ct).Value is not int startIndex) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "IndexOf" } method) return;
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
        var valid = count is null
            ? startIndex >= 0 && startIndex <= length
            : startIndex >= 0 && count >= 0 && startIndex + count <= length;
        if (valid) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), receiverValue, length));
    }
}
