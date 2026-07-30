using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто шестьдесят восьмой проход, batch) — own pick, not a Roslynator port.
// `s + ""` (or `"" + s`) is a no-op when `s` is already a string — concatenating an empty string onto
// a string returns the string unchanged. Deliberately requires the OTHER side's static type to already
// be string — `n + ""` where n is a number is the common (and legitimate) "convert to string" idiom,
// not a redundancy, so that case is left alone.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantStringConcatWithEmptyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0110";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant concatenation with an empty string",
        messageFormat: "Concatenating with an empty string literal is a no-op — remove the '+ \"\"'",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Concatenating an already-string value with an empty string literal returns the value unchanged.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.AddExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var binary = (BinaryExpressionSyntax)context.Node;
        var nonEmptySide = IsEmptyStringLiteral(binary.Right) ? binary.Left
            : IsEmptyStringLiteral(binary.Left) ? binary.Right
            : null;
        if (nonEmptySide is null) return;

        var nonEmptyType = context.SemanticModel.GetTypeInfo(nonEmptySide, context.CancellationToken).Type;
        if (nonEmptyType is not { SpecialType: SpecialType.System_String }) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, binary.GetLocation()));
    }

    private static bool IsEmptyStringLiteral(ExpressionSyntax expression) =>
        expression is LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } literal
        && literal.Token.ValueText.Length == 0;
}
