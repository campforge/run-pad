using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (сто сорок девятый проход, batch) — idea ported from Roslynator's
// UseMethodGroupInsteadOfAnonymousFunctionAnalyzer / explicit-delegate-creation family (Apache
// License 2.0, https://github.com/dotnet/roslynator). `new EventHandler(OnClick)` (explicitly
// constructing a delegate from a method group/lambda) is always redundant — C# converts a method
// group or lambda to a compatible delegate type directly, with no explicit constructor call needed,
// in every context this could legally appear.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RemoveRedundantDelegateCreationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0029";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant explicit delegate creation",
        messageFormat: "Explicit delegate creation is redundant — the method group/lambda can be used directly",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "A method group or lambda converts to a compatible delegate type directly, with no explicit constructor call needed.");

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
        if (creation.ArgumentList is not { Arguments.Count: 1 }) return;

        var type = context.SemanticModel.GetTypeInfo(creation, context.CancellationToken).Type;
        if (type is INamedTypeSymbol { TypeKind: TypeKind.Delegate })
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }
}
