using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста одиннадцатый проход, batch) — own pick, not a Roslynator port. Same "literal
// receiver, known length" theme as InvalidStringRemoveOrInsertArgumentAnalyzer (RPAD0281, проход 298),
// applied to `StringBuilder` instead of `string`: `new StringBuilder("literal").Remove(startIndex,
// length)`/`.Insert(index, value)` throw ArgumentOutOfRangeException when the arguments put them outside
// the literal's own known length — provable because the receiver is a FRESHLY CREATED StringBuilder
// whose initial content is a string literal, so its exact starting length is known at analysis time,
// same principle as RPAD0281 but for a constructor call instead of a bare string literal (confirmed
// empirically via scratch `dotnet run` script, not assumed). Scoped to the `StringBuilder(string)`
// constructor overload specifically — `StringBuilder(int capacity)` and other overloads don't establish
// a known CONTENT length (capacity is not length), so those are not recognized here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringBuilderRemoveOrInsertArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0288";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "StringBuilder Remove/Insert argument is out of range for a literal-initialized receiver",
        messageFormat: "'new StringBuilder(\"{0}\").{1}(...)' always throws ArgumentOutOfRangeException at runtime — the receiver has only {2} character(s)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StringBuilder.Remove(startIndex, length) and StringBuilder.Insert(index, value) throw ArgumentOutOfRangeException when startIndex (plus length, for Remove) exceeds the StringBuilder's initial content length, or either numeric argument is negative.");

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
                Expression: BaseObjectCreationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var contentArg }] } receiverCreation,
            } memberAccess)
        {
            return;
        }
        var methodName = memberAccess.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(contentArg, ct).Value is not string receiverValue) return;

        if (invocation.ArgumentList.Arguments is not [{ Expression: var startArg }, { Expression: var secondArg }])
            return;
        if (!TryGetIntLiteralValue(startArg, out var startIndex)) return;

        bool inRange;
        if (methodName == "Remove")
        {
            if (!TryGetIntLiteralValue(secondArg, out var length)) return;
            inRange = startIndex >= 0 && length >= 0 && startIndex + length <= receiverValue.Length;
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
        if (method.ContainingType?.ToDisplayString() != "System.Text.StringBuilder") return;
        if (context.SemanticModel.GetSymbolInfo(receiverCreation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
            || ctor.ContainingType?.ToDisplayString() != "System.Text.StringBuilder")
        {
            return;
        }

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
