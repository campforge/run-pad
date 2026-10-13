using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port.
// `char.IsLetter(c) || char.IsDigit(c)` re-checks and re-reads `c` twice to ask "is c alphanumeric" —
// `char.IsLetterOrDigit(c)` already exists and says exactly that in one call.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class PreferIsLetterOrDigitAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0126";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Prefer char.IsLetterOrDigit over separate IsLetter/IsDigit checks",
        messageFormat: "'char.IsLetter(...) || char.IsDigit(...)' can be simplified to 'char.IsLetterOrDigit(...)'",
        category: "Simplification",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "char.IsLetter(c) || char.IsDigit(c) re-checks c twice for exactly what char.IsLetterOrDigit(c) already answers in one call.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.LogicalOrExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;

        var letterArg = ExtractCharCheckArgument(context, binary.Left, "IsLetter") ?? ExtractCharCheckArgument(context, binary.Right, "IsLetter");
        var digitArg = ExtractCharCheckArgument(context, binary.Left, "IsDigit") ?? ExtractCharCheckArgument(context, binary.Right, "IsDigit");
        if (letterArg is null || digitArg is null) return;
        if (!letterArg.IsEquivalentTo(digitArg)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static ExpressionSyntax? ExtractCharCheckArgument(SyntaxNodeAnalysisContext context, ExpressionSyntax expression, string expectedMethodName)
    {
        if (expression is not InvocationExpressionSyntax { ArgumentList.Arguments: [{ Expression: var argument }] } invocation)
            return null;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } || methodName != expectedMethodName)
            return null;

        if (context.SemanticModel.GetSymbolInfo(invocation, context.CancellationToken).Symbol is not IMethodSymbol { IsStatic: true } method)
            return null;
        if (method.ContainingType?.SpecialType != SpecialType.System_Char) return null;

        return argument;
    }
}
