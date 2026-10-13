using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator's
// UnnecessaryInterpolatedStringAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator).
// `$"{s}"` where `s`'s static type is already `string`, and there's no surrounding text or
// format/alignment clause, is exactly `s` — the interpolation machinery does nothing here.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UnnecessaryInterpolatedStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0030";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Unnecessary interpolated string",
        messageFormat: "This interpolated string is redundant — the interpolated expression is already a string",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An interpolated string whose sole content is an already-string expression, with no surrounding text or format clause, is exactly that expression.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InterpolatedStringExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var interpolated = (InterpolatedStringExpressionSyntax)context.Node;
        if (interpolated.Contents is not [InterpolationSyntax { FormatClause: null, AlignmentClause: null } interpolation])
            return;

        var type = context.SemanticModel.GetTypeInfo(interpolation.Expression, context.CancellationToken).Type;
        if (type is { SpecialType: SpecialType.System_String })
            context.ReportDiagnostic(Diagnostic.Create(Rule, interpolated.GetLocation()));
    }
}
