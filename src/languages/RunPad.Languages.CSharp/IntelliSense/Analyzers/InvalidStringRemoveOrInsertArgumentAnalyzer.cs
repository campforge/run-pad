using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as SubstringOutOfRangeAnalyzer (RPAD0235), applied to the two
// other string methods with the same shape — `"literal".Remove(startIndex, count)` and
// `"literal".Insert(startIndex, value)` — both throw ArgumentOutOfRangeException when the arguments put
// them outside the literal's own known length, provable purely from the receiver being a string literal
// (confirmed empirically via scratch `dotnet run` script, not assumed). Deliberately EXCLUDES the
// single-argument `"literal".Remove(startIndex)` overload — PROBEd alongside this one and confirmed it
// collides with a built-in "Remove can be simplified" hint that fires on every one-argument Remove call
// regardless of whether it would throw, the same collision RPAD0235 already documented for Substring's
// one-argument overload; the built-in hint does NOT fire on the two-argument Remove or on Insert, leaving
// both clear here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringRemoveOrInsertArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0281";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Remove/Insert argument is out of range for a literal receiver",
        messageFormat: "'\"{0}\".{1}(...)' always throws ArgumentOutOfRangeException at runtime — the receiver has only {2} character(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "string.Remove(startIndex, count) and string.Insert(startIndex, value) throw ArgumentOutOfRangeException when startIndex (plus count, for Remove) exceeds the string's length, or either numeric argument is negative.");

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
                Name.Identifier.Text: "Remove" or "Insert",
                Expression: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } receiverLiteral,
            } memberAccess)
        {
            return;
        }
        var methodName = memberAccess.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var startArg }, { Expression: var secondArg }])
            return;
        if (!TryGetIntLiteralValue(startArg, out var startIndex)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(receiverLiteral, ct).Value is not string receiverValue)
            return;

        bool inRange;
        if (methodName == "Remove")
        {
            if (!TryGetIntLiteralValue(secondArg, out var count)) return;
            inRange = startIndex >= 0 && count >= 0 && startIndex + count <= receiverValue.Length;
        }
        else
        {
            inRange = startIndex >= 0 && startIndex <= receiverValue.Length;
        }

        if (inRange) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.SpecialType != SpecialType.System_String) return;

        context.ReportDiagnostic(Diagnostic.Create(
            Rule, invocation.GetLocation(), receiverValue, methodName, receiverValue.Length));
    }

    private static bool TryGetIntLiteralValue(ExpressionSyntax expression, out int value)
    {
        value = 0;
        if (expression is PrefixUnaryExpressionSyntax { RawKind: (int)SyntaxKind.UnaryMinusExpression, Operand: LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } negativeLiteral })
        {
            if (negativeLiteral.Token.Value is not int positiveValue) return false;
            value = -positiveValue;
            return true;
        }
        if (expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal && literal.Token.Value is int literalValue)
        {
            value = literalValue;
            return true;
        }
        return false;
    }
}
