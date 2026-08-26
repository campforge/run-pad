using System.Collections.Immutable;
using System.IO.Pipelines;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (453-й проход, batch) — candidate #2 from the fifteenth research subagent survey,
// re-verified independently before implementing. `System.IO.Pipelines.PipeOptions`'s
// `pauseWriterThreshold`/`resumeWriterThreshold` constructor parameters — the first genuinely
// TWO-JOINT-LITERAL analyzer in this catalog whose validity depends on BOTH literal arguments together,
// not just one independently. Confirmed empirically: `ArgumentOutOfRangeException` when
// `pauseWriterThreshold < -1` (regardless of `resumeWriterThreshold`'s value), OR when
// `pauseWriterThreshold > 0` and `resumeWriterThreshold` is either negative or greater than
// `pauseWriterThreshold` itself. Confirmed valid combinations worth noting for correct test literals:
// equal thresholds (`100, 100`), a zero `resumeWriterThreshold` with any positive pause threshold
// (`100, 0`), a zero `pauseWriterThreshold` with `resumeWriterThreshold` up to `int.MaxValue` (disabling
// the pause threshold effectively removes the upper-bound check on resume), and `pauseWriterThreshold ==
// -1` (disables backpressure entirely) with any non-negative resume threshold. Confirmed invariant across
// en-US/de-DE/ar-SA/ja-JP. No side effects — `PipeOptions` is a pure managed configuration object,
// construction never allocates a real pipe/buffer. Self-caught correctness bug before shipping: the
// constructor's REAL default for BOTH parameters is `-1` (confirmed via reflection) — an initial draft
// defaulted an unseen parameter to `-1` for the probe without distinguishing "argument syntactically
// OMITTED entirely" (safe, `-1` genuinely is what the real call would use) from "argument PRESENT but
// not a compile-time constant" (e.g. `resumeWriterThreshold: someVariable` — must bail out, since
// guessing `-1` there would misreport a call whose real runtime value might be perfectly valid). Fixed by
// tracking an explicit "was this parameter's argument seen at all" flag per parameter, separate from the
// extracted `int?` value.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPipeOptionsConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0454";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "PipeOptions writer-threshold arguments are invalid together",
        messageFormat: "'new PipeOptions(...)' always throws ArgumentOutOfRangeException at runtime — pauseWriterThreshold ({0}) and resumeWriterThreshold ({1}) are not a valid combination",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PipeOptions throws ArgumentOutOfRangeException when pauseWriterThreshold < -1, or when pauseWriterThreshold > 0 and resumeWriterThreshold is negative or greater than pauseWriterThreshold.");

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
        if (creation.ArgumentList is not { } argumentList) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
            } ctor)
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.IO.Pipelines.PipeOptions") return;

        // Distinguish an argument syntactically OMITTED entirely (safe to default to -1, the real
        // constructor's own default parameter value for both) from one that's PRESENT but not a
        // compile-time constant (must bail out entirely rather than guessing a default — the omitted
        // case and the "provided a variable" case both leave a literal `int?` null, so this must be
        // tracked as a separate "was this parameter seen at all" flag, not inferred from the value).
        bool pauseSeen = false, resumeSeen = false;
        int pause = -1, resume = -1;
        for (var i = 0; i < argumentList.Arguments.Count; i++)
        {
            var arg = argumentList.Arguments[i];
            var paramName = arg.NameColon?.Name.Identifier.Text ?? (i < ctor.Parameters.Length ? ctor.Parameters[i].Name : null);
            if (paramName != "pauseWriterThreshold" && paramName != "resumeWriterThreshold") continue;

            if (context.SemanticModel.GetConstantValue(arg.Expression, ct).Value is not int value) return;
            if (paramName == "pauseWriterThreshold") { pause = value; pauseSeen = true; }
            else { resume = value; resumeSeen = true; }
        }
        if (!pauseSeen && !resumeSeen) return;

        try
        {
            _ = new PipeOptions(pauseWriterThreshold: pause, resumeWriterThreshold: resume);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), pause, resume));
        }
    }
}
