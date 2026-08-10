using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят второй проход, batch) — own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidBarrierConstructorArgumentAnalyzer family (RPAD0323,
// проход 350): `new BlockingCollection<T>(int boundedCapacity)` throws ArgumentOutOfRangeException when
// `boundedCapacity` isn't strictly positive — confirmed empirically via scratch `dotnet run` script
// (unlike most "capacity" parameters elsewhere in this catalog where zero is valid, `boundedCapacity`
// must be > 0, the same "must be strictly positive" rule as `SemaphoreSlim`'s `maxCount` — RPAD0319).
// Scoped to the single-arg `(boundedCapacity)` overload — the 2-arg overload adds an explicit backing
// `IProducerConsumerCollection<T>` that doesn't change this parameter's own validation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBlockingCollectionConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0333";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BlockingCollection boundedCapacity argument is not positive",
        messageFormat: "'new BlockingCollection<...>(...)' always throws ArgumentOutOfRangeException at runtime — boundedCapacity must be positive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new BlockingCollection<T>(boundedCapacity) throws ArgumentOutOfRangeException when boundedCapacity isn't strictly positive.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var capacityArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(capacityArg, ct).Value is not int boundedCapacity || boundedCapacity >= 1)
            return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Concurrent.BlockingCollection<T>")
            return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }]) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }
}
