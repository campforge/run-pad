using System.Collections.Immutable;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (454-й проход, batch) — candidate #3 from the sixteenth research subagent survey,
// re-verified independently before implementing. `BatchBlock<T>(int batchSize)` and
// `BatchedJoinBlock<T1,T2>(int batchSize)`/`BatchedJoinBlock<T1,T2,T3>(int batchSize)` constructors —
// confirmed empirically all throw `ArgumentOutOfRangeException` ("Number must be positive") for `0` or a
// negative `batchSize`; any positive value is valid. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
// No side effects — constructing (and discarding) a block never schedules any dataflow work, same
// confirmation already established for `InvalidDataflowBlockOptionsPropertyAnalyzer` (RPAD0457).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBatchBlockConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0458";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Dataflow batch block batchSize argument always throws",
        messageFormat: "'new {0}(...)' always throws ArgumentOutOfRangeException at runtime — batchSize must be positive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BatchBlock<T>/BatchedJoinBlock<T1,T2>/BatchedJoinBlock<T1,T2,T3>'s batchSize constructor argument throws ArgumentOutOfRangeException for a value that isn't strictly positive.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var batchSizeArg }, ..] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(batchSizeArg, ct).Value is not int batchSize) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }, ..],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        var genericTypeName = containingType.OriginalDefinition.ToDisplayString();
        var shortName = genericTypeName switch
        {
            "System.Threading.Tasks.Dataflow.BatchBlock<T>" => "BatchBlock<T>",
            "System.Threading.Tasks.Dataflow.BatchedJoinBlock<T1, T2>" => "BatchedJoinBlock<T1, T2>",
            "System.Threading.Tasks.Dataflow.BatchedJoinBlock<T1, T2, T3>" => "BatchedJoinBlock<T1, T2, T3>",
            _ => null,
        };
        if (shortName is null) return;

        try
        {
            switch (shortName)
            {
                case "BatchBlock<T>": _ = new BatchBlock<object>(batchSize); break;
                case "BatchedJoinBlock<T1, T2>": _ = new BatchedJoinBlock<object, object>(batchSize); break;
                default: _ = new BatchedJoinBlock<object, object, object>(batchSize); break;
            }
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), shortName));
        }
    }
}
