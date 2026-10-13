using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidSemaphoreConstructorArgumentAnalyzer (RPAD0322): `new
// ConcurrentDictionary<TKey, TValue>(int concurrencyLevel, int capacity)` throws
// ArgumentOutOfRangeException when `capacity` is negative, or when `concurrencyLevel` is neither
// positive NOR exactly -1 — confirmed empirically via scratch `dotnet run` script, including the
// genuinely surprising part: `concurrencyLevel == -1` is itself a documented sentinel meaning "use the
// default level" and does NOT throw (a `Timeout.Infinite`-style `-1` convention, but for a totally
// unrelated parameter — worth confirming empirically rather than assuming zero/negative always means
// "invalid" in this catalog). Scoped to the 2-arg `(concurrencyLevel, capacity)` overload — the 3-/4-arg
// overloads add a collection/comparer argument that doesn't change these two parameters' validation.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidConcurrentDictionaryConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0332";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ConcurrentDictionary constructor arguments are invalid",
        messageFormat: "'new ConcurrentDictionary<...>(...)' always throws ArgumentOutOfRangeException at runtime — {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new ConcurrentDictionary<TKey, TValue>(concurrencyLevel, capacity) throws ArgumentOutOfRangeException when capacity is negative, or when concurrencyLevel is neither positive nor exactly -1 (the 'use default' sentinel).");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var concurrencyArg }, { Expression: var capacityArg }, ..] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(concurrencyArg, ct).Value is not int concurrencyLevel) return;
        if (context.SemanticModel.GetConstantValue(capacityArg, ct).Value is not int capacity) return;

        string reason;
        if (concurrencyLevel < 1 && concurrencyLevel != -1)
        {
            reason = "concurrencyLevel must be positive, or exactly -1 to use the default level";
        }
        else if (capacity < 0)
        {
            reason = "capacity cannot be negative";
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.OriginalDefinition.ToDisplayString() != "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>")
            return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }, ..])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), reason));
    }
}
