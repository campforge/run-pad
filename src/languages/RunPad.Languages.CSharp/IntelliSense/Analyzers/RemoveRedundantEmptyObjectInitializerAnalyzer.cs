using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок восьмой проход, batch) — idea ported from Roslynator's
// RemoveRedundantEmptyObjectInitializerAnalyzer (Apache License 2.0,
// https://github.com/dotnet/roslynator). `new Foo() { }` — an object initializer block with no
// member assignments in it — behaves identically to `new Foo()` without one; pure clutter.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantEmptyObjectInitializerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0026";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant empty object initializer",
        messageFormat: "Empty object initializer '{ }' can be removed",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An object initializer with no member assignments behaves identically to not having one.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (ObjectCreationExpressionSyntax)context.Node;
        if (creation.Initializer is { Expressions.Count: 0 } initializer)
            context.ReportDiagnostic(Diagnostic.Create(Rule, initializer.GetLocation()));
    }
}
