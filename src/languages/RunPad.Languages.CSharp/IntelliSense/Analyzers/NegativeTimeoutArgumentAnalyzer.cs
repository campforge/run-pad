using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231)/
// NegativeCountArgumentAnalyzer (RPAD0232), for millisecond-timeout
// parameters instead of a capacity/count: `Task.Delay(ms)`/`Thread.Sleep(ms)`/`new
// CancellationTokenSource(ms)` all treat `-1` as the special "Timeout.Infinite" sentinel, but throw
// `ArgumentOutOfRangeException` for any OTHER negative value — a different threshold than
// RPAD0231/RPAD0232's plain "must be non-negative", which is why this is its own analyzer with its own
// message rather than a shared one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeTimeoutArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0233";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Timeout below -1 (Infinite) always throws ArgumentOutOfRangeException",
        messageFormat: "'{0}' always throws ArgumentOutOfRangeException at runtime — a millisecond timeout must be -1 (Timeout.Infinite) or non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Task.Delay, Thread.Sleep, and the CancellationTokenSource(int) constructor treat -1 as Timeout.Infinite but throw ArgumentOutOfRangeException for any other negative millisecond value.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.InvocationExpression, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        ArgumentListSyntax? argumentList;
        string displayText;
        ISymbol? symbol;
        var ct = context.CancellationToken;

        switch (context.Node)
        {
            case InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.Text: "Delay" or "Sleep" } access } invocation:
                argumentList = invocation.ArgumentList;
                displayText = $"{access.Expression}.{access.Name.Identifier.Text}({{0}})";
                symbol = context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol;
                break;
            case BaseObjectCreationExpressionSyntax creation:
                argumentList = creation.ArgumentList;
                displayText = "new CancellationTokenSource({0})";
                symbol = context.SemanticModel.GetSymbolInfo(creation, ct).Symbol;
                break;
            default:
                return;
        }

        if (argumentList is not { Arguments: [{ Expression: var timeoutArg }, ..] }) return;
        if (!TryGetIntLessThanNegativeOne(timeoutArg, out var value)) return;

        if (symbol is not IMethodSymbol method) return;
        var isMatch = method switch
        {
            { MethodKind: MethodKind.Ordinary, Name: "Delay" } => method.ContainingType?.ToDisplayString() == "System.Threading.Tasks.Task",
            { MethodKind: MethodKind.Ordinary, Name: "Sleep" } => method.ContainingType?.ToDisplayString() == "System.Threading.Thread",
            { MethodKind: MethodKind.Constructor } => method.ContainingType?.ToDisplayString() == "System.Threading.CancellationTokenSource",
            _ => false,
        };
        if (!isMatch) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), string.Format(displayText, value)));
    }

    private static bool TryGetIntLessThanNegativeOne(ExpressionSyntax expression, out int value)
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
        return value < -1;
    }
}
