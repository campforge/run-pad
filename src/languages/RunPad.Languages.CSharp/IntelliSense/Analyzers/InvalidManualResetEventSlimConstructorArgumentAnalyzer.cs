using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста пятьдесят первый проход, batch) — own pick, not a Roslynator port. Same
// "constructor argument validation" theme as InvalidSemaphoreConstructorArgumentAnalyzer (RPAD0322,
// проход 350): `new ManualResetEventSlim(bool initialState, int spinCount)` throws
// ArgumentOutOfRangeException when `spinCount` is negative or greater than 2047 (the documented,
// hardcoded upper bound — confirmed empirically via scratch `dotnet run` script, not assumed). A
// managed-only construct (no live OS handle at construction time, same reasoning as
// InvalidSemaphoreSlimConstructorArgumentAnalyzer/RPAD0319), so this one DOES use the "construct the
// real object inside a try/catch" technique rather than hand-coding the boundary — the constructed
// instance is immediately eligible for GC, matching this project's precedent for managed-only throwaway
// probes.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidManualResetEventSlimConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0328";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ManualResetEventSlim spinCount argument is out of range",
        messageFormat: "'new ManualResetEventSlim(...)' always throws ArgumentOutOfRangeException at runtime — spinCount must be between 0 and 2047",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new ManualResetEventSlim(initialState, spinCount) throws ArgumentOutOfRangeException when spinCount is negative or greater than 2047.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var initialStateArg }, { Expression: var spinCountArg }] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(initialStateArg, ct).Value is not bool initialState) return;
        if (context.SemanticModel.GetConstantValue(spinCountArg, ct).Value is not int spinCount) return;
        if (!AlwaysThrows(initialState, spinCount)) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Threading.ManualResetEventSlim") return;
        if (ctor.Parameters is not [{ Type.SpecialType: SpecialType.System_Boolean }, { Type.SpecialType: SpecialType.System_Int32 }])
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static bool AlwaysThrows(bool initialState, int spinCount)
    {
        try
        {
            using var mres = new System.Threading.ManualResetEventSlim(initialState, spinCount);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
