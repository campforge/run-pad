using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the real
// validator" theme as TimeSpanConstructorOverflowAnalyzer (RPAD0314, проход 347): `new
// SemaphoreSlim(int initialCount[, int maxCount])` throws ArgumentOutOfRangeException for a negative
// `initialCount`, a negative or zero `maxCount` (confirmed empirically — `maxCount` must be strictly
// positive, unlike most "count" parameters elsewhere in this catalog where zero is valid), or an
// `initialCount` greater than `maxCount` — confirmed empirically via scratch `dotnet run` script, not
// assumed. Rather than hand-coding these three independent conditions, this analyzer constructs the
// REAL `SemaphoreSlim` inside a try/catch, same reasoning as the rest of the "run the real validator"
// family in this catalog — the constructed instance is immediately eligible for GC (never `.Dispose()`d
// or otherwise used), matching this project's precedent of using throwaway constructions purely to probe
// validation logic (e.g. RPAD0279's throwaway `Regex`). Scoped to LITERAL integer arguments only.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSemaphoreSlimConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0319";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SemaphoreSlim constructor arguments are invalid",
        messageFormat: "'new SemaphoreSlim(...)' always throws ArgumentOutOfRangeException at runtime — initialCount must be non-negative, maxCount must be positive, and initialCount must not exceed maxCount",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new SemaphoreSlim(initialCount[, maxCount]) throws ArgumentOutOfRangeException when initialCount is negative, maxCount is not positive, or initialCount exceeds maxCount.");

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
        if (creation.ArgumentList is not { Arguments: { Count: 1 or 2 } args }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Threading.SemaphoreSlim") return;
        if (!ctor.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32)) return;

        if (context.SemanticModel.GetConstantValue(args[0].Expression, ct).Value is not int initialCount) return;
        int? maxCount = null;
        if (args.Count == 2)
        {
            if (context.SemanticModel.GetConstantValue(args[1].Expression, ct).Value is not int mc) return;
            maxCount = mc;
        }

        if (!AlwaysThrows(initialCount, maxCount)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static bool AlwaysThrows(int initialCount, int? maxCount)
    {
        try
        {
            _ = maxCount is { } mc ? new System.Threading.SemaphoreSlim(initialCount, mc) : new System.Threading.SemaphoreSlim(initialCount);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
