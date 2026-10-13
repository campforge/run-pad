using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal argument that always throws" theme as InvalidGuidLiteralFormatAnalyzer (RPAD0246): `Version.Parse(s)`
// requires `s` to be 2 to 4 dot-separated non-negative integer components
// ("major.minor[.build[.revision]]") — fewer than 2 components, more than 4, or any non-numeric
// component always throws (`ArgumentException` for the component-count cases, `FormatException` for a
// non-numeric component). A plain regex check against this shape is unambiguous — unlike Guid's five
// overlapping formats, Version has exactly one.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidVersionLiteralFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0247";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Version.Parse argument does not match the major.minor[.build[.revision]] format",
        messageFormat: "'Version.Parse(...)' always throws at runtime — this literal doesn't have 2 to 4 dot-separated non-negative integer components (major.minor[.build[.revision]])",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Version.Parse requires 2 to 4 dot-separated non-negative integer components; anything else throws ArgumentException or FormatException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly Regex ValidVersionPattern = new(
        @"^\d+\.\d+(\.\d+(\.\d+)?)?$",
        RegexOptions.Compiled);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;
        if (ValidVersionPattern.IsMatch(value)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Version") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation()));
    }
}
