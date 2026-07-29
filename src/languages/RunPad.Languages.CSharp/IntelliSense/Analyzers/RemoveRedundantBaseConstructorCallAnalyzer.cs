using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок восьмой проход, batch) — idea ported from Roslynator's
// RemoveRedundantBaseConstructorCallAnalyzer (Apache License 2.0, https://github.com/dotnet/roslynator).
// An explicit `: base()` with no arguments is exactly what the compiler already inserts implicitly
// when the initializer is omitted — writing it out adds nothing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantBaseConstructorCallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0021";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant base constructor call",
        messageFormat: "': base()' with no arguments is exactly what the compiler already inserts implicitly — it can be removed",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An explicit, argument-less ': base()' adds nothing over omitting the initializer entirely.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.BaseConstructorInitializer);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var initializer = (ConstructorInitializerSyntax)context.Node;
        if (initializer.ArgumentList.Arguments.Count == 0)
            context.ReportDiagnostic(Diagnostic.Create(Rule, initializer.GetLocation()));
    }
}
