using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the thirty-first research subagent survey,
// re-verified independently before implementing. `System.Runtime.InteropServices.GCHandle.Alloc(object?,
// GCHandleType)` — first touch of `GCHandle`. Confirmed empirically: throws `ArgumentOutOfRangeException`
// ("Enum value was out of legal range.", param `type`) whenever `type` is outside the defined
// `GCHandleType` range `0..3` (`Weak`/`WeakTrackResurrection`/`Normal`/`Pinned`); confirmed `int`-backed
// via `Enum.GetUnderlyingType`. Confirmed side-effect-safe to probe: the exception fires before any
// handle is actually allocated (verified by running the invalid-enum probe 200,000 times in a loop with
// negligible memory delta and no observable handle leak — same managed-only-until-the-exception-point
// shape already accepted for `InvalidManualResetEventSlimConstructorArgumentAnalyzer`/RPAD0328). No
// `[SupportedOSPlatform]` gating. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidGCHandleAllocTypeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0506";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "GCHandle.Alloc type argument is not a defined GCHandleType",
        messageFormat: "'GCHandle.Alloc(..., {0})' always throws ArgumentOutOfRangeException at runtime — the value is not a defined GCHandleType member",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "GCHandle.Alloc(object?, GCHandleType) throws ArgumentOutOfRangeException when the GCHandleType argument is outside its defined 0-3 range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Alloc" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Name: "Alloc",
                IsStatic: true,
                ContainingType: { } containingType,
                Parameters: [_, { Type.Name: "GCHandleType" }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Runtime.InteropServices.GCHandle") return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var typeArg }]) return;
        if (context.SemanticModel.GetConstantValue(typeArg, ct).Value is not int typeRaw) return;

        try
        {
            var handle = GCHandle.Alloc(new object(), (GCHandleType)typeRaw);
            handle.Free();
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), typeRaw));
        }
    }
}
