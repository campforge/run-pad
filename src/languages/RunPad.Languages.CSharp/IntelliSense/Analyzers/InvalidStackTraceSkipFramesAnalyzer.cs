using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (463-й проход, batch) — candidate #1 from the twenty-sixth research subagent survey,
// re-verified independently before implementing. `System.Diagnostics.StackTrace` constructors —
// `StackTrace(int skipFrames)`, `StackTrace(int skipFrames, bool)`, `StackTrace(Exception, int
// skipFrames)`, `StackTrace(Exception, int skipFrames, bool)` — all four bundled under one diagnostic
// (precedent RPAD0426). Confirmed empirically: a negative `skipFrames` always throws
// `ArgumentOutOfRangeException` (param `skipFrames`); `0` is valid, no sentinel exception (unlike
// `MemoryPool<T>.Rent`'s `-1`). Confirmed no `[SupportedOSPlatform]` gating (reflection: only
// nullable-context attributes) and no side effects — capturing a stack trace is pure/in-memory, the
// negative-arg path throws before any frame walking. Confirmed invariant across en-US/de-DE/ar-SA/
// ja-JP (only message digit glyphs differ under ar-SA, not the exception type/behavior). Grepped the
// analyzer directory first per the standing duplicate-check discipline — the only existing
// `StackTrace`-related hits are about the `throw ex;`-resets-stack-trace C# idiom, unrelated to this
// type's constructors.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStackTraceSkipFramesAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0489";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "StackTrace skipFrames argument is negative",
        messageFormat: "'new StackTrace(...)' always throws ArgumentOutOfRangeException at runtime — skipFrames must be non-negative",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StackTrace's int-skipFrames constructors throw ArgumentOutOfRangeException for a negative skipFrames value.");

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
        if (context.SemanticModel.GetSymbolInfo(creation, context.CancellationToken).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                ContainingType: { } containingType,
            } ctor)
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Diagnostics.StackTrace") return;

        var skipFramesIndex = -1;
        for (var i = 0; i < ctor.Parameters.Length; i++)
        {
            if (ctor.Parameters[i].Name != "skipFrames") continue;
            skipFramesIndex = i;
            break;
        }
        if (skipFramesIndex < 0) return;
        if (creation.ArgumentList is not { } argumentList || argumentList.Arguments.Count <= skipFramesIndex)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(argumentList.Arguments[skipFramesIndex].Expression, ct).Value is not int skipFrames) return;
        if (skipFrames >= 0) return;

        try
        {
            _ = new StackTrace(skipFrames);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
        }
    }
}
