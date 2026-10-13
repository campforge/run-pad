using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run the real
// validator" theme as DateTimeOrDateOnlyAddMethodOverflowAnalyzer (RPAD0313, проход 346): the
// `TimeSpan(int hours, int minutes, int seconds)`/`(int days, int hours, int minutes, int seconds)`/
// `(..., int milliseconds)`/`(..., int microseconds)` constructor overloads throw
// ArgumentOutOfRangeException when the resulting duration overflows `TimeSpan`'s representable range —
// confirmed empirically via scratch `dotnet run` script, not assumed. Deliberately does NOT cover the
// single-argument `TimeSpan(long ticks)` overload — confirmed via the same empirical check that it can
// NEVER overflow (every `long` value is itself a valid tick count, since `TimeSpan.MaxValue.Ticks ==
// long.MaxValue`), so there's nothing for this analyzer to add there. Scoped to LITERAL integer
// constructor arguments only, and validates by constructing the REAL `TimeSpan` inside a try/catch
// rather than hand-rolling the overflow arithmetic — same reasoning as RPAD0313.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class TimeSpanConstructorOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0314";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TimeSpan constructor always overflows",
        messageFormat: "'new TimeSpan(...)' always throws ArgumentOutOfRangeException at runtime — the result is outside TimeSpan's representable range",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The (hours, minutes, seconds), (days, hours, minutes, seconds), and longer TimeSpan constructor overloads throw ArgumentOutOfRangeException when the resulting duration overflows TimeSpan's representable range.");

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
        if (creation.ArgumentList is not { Arguments: { Count: 3 or 4 or 5 or 6 } args }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.TimeSpan") return;
        if (!ctor.Parameters.All(p => p.Type.SpecialType == SpecialType.System_Int32)) return;

        var values = new int[args.Count];
        for (var i = 0; i < args.Count; i++)
        {
            if (context.SemanticModel.GetConstantValue(args[i].Expression, ct).Value is not int v) return;
            values[i] = v;
        }

        if (!AlwaysThrows(values)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static bool AlwaysThrows(int[] v)
    {
        try
        {
            _ = v.Length switch
            {
                3 => new TimeSpan(v[0], v[1], v[2]),
                4 => new TimeSpan(v[0], v[1], v[2], v[3]),
                5 => new TimeSpan(v[0], v[1], v[2], v[3], v[4]),
                _ => new TimeSpan(v[0], v[1], v[2], v[3], v[4], v[5]),
            };
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
