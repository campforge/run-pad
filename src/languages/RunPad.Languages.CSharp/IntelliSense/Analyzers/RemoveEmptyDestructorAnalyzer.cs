using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Idea ported from Roslynator (Apache License 2.0,
// https://github.com/dotnet/roslynator). An empty destructor (`~C() { }`) still forces the object onto
// the finalization queue — a real (if minor) GC cost for something that does nothing.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveEmptyDestructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0010";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Empty destructor",
        messageFormat: "Empty destructor can be removed",
        category: "Performance",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "An empty destructor still forces the object onto the finalization queue for no benefit.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.DestructorDeclaration);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var destructor = (DestructorDeclarationSyntax)context.Node;
        if (destructor.Body is { Statements.Count: 0 })
            context.ReportDiagnostic(Diagnostic.Create(Rule, destructor.Identifier.GetLocation()));
    }
}
