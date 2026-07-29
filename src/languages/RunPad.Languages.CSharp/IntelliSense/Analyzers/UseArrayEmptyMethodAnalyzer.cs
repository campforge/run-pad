using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто пятидесятый проход, batch) — idea ported from Roslynator's
// UseArrayEmptyMethodAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator). `new T[0]`
// allocates a fresh, distinct zero-length array object every time it runs; `Array.Empty<T>()` returns
// the same cached singleton instance for a given T, allocating nothing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UseArrayEmptyMethodAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0034";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Use Array.Empty<T>()",
        messageFormat: "Use 'Array.Empty<T>()' instead of 'new T[0]' — it returns a cached instance instead of allocating a new one",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "'new T[0]' allocates a fresh array object every time; Array.Empty<T>() returns the same cached instance.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ArrayCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var arrayCreation = (ArrayCreationExpressionSyntax)context.Node;
        if (arrayCreation.Initializer is not null) return; // require the bare "new T[0]" form only

        if (arrayCreation.Type.RankSpecifiers is not [{ Sizes: [var size] }]) return;
        if (size is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } literal
            || literal.Token.ValueText != "0")
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(Rule, arrayCreation.GetLocation()));
    }
}
