using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "negative
// literal argument always throws" theme as NegativeCapacityArgumentAnalyzer/NegativeArrayPoolRentLengthAnalyzer
// (RPAD0231/RPAD0295), on `Console.SetCursorPosition(int left, int top)` — confirmed empirically (scratch
// `dotnet run` script, since deleted, on this project's Linux dev environment) to throw
// ArgumentOutOfRangeException when EITHER `left` or `top` is negative, independently of the other.
// Deliberately does NOT extend to `Console.SetBufferSize`/`SetWindowSize`/`SetWindowPosition`, considered
// alongside this during brainstorm and rejected: those three threw `PlatformNotSupportedException`
// instead in the same empirical check (Linux/macOS terminals don't support programmatic buffer/window
// resizing at all) — an analyzer claiming they "always throw ArgumentOutOfRangeException" would be
// actively WRONG on non-Windows, where this project's own CI runs (ubuntu/windows/macos). Scoped to a
// LITERAL negative integer for at least one of the two arguments — same reasoning as the rest of this
// family; the other argument need not be a literal at all (its value doesn't change the conclusion that
// the call always throws).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NegativeConsoleCursorPositionArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0296";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Negative Console.SetCursorPosition argument always throws",
        messageFormat: "'Console.SetCursorPosition({0}, {1})' always throws ArgumentOutOfRangeException at runtime — left and top cannot be negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Console.SetCursorPosition(int left, int top) throws ArgumentOutOfRangeException when left or top is negative.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SetCursorPosition" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var leftArg }, { Expression: var topArg }])
            return;

        var ct = context.CancellationToken;
        var leftIsNegative = context.SemanticModel.GetConstantValue(leftArg, ct).Value is int left && left < 0;
        var topIsNegative = context.SemanticModel.GetConstantValue(topArg, ct).Value is int top && top < 0;
        if (!leftIsNegative && !topIsNegative) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "SetCursorPosition" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Console") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), leftArg.ToString(), topArg.ToString()));
    }
}
