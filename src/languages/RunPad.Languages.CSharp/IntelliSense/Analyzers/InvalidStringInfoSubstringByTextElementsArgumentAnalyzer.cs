using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `StringInfo.SubstringByTextElements(int
// startingTextElement)` / `(int startingTextElement, int lengthInTextElements)`. Confirmed empirically:
// a NEGATIVE `startingTextElement` or `lengthInTextElements` always throws `ArgumentOutOfRangeException`
// with a `ParamName` matching the offending parameter's own name, regardless of the actual text the
// `StringInfo` was constructed from — confirmed identically against a short and a much longer string
// (trap #8 checked). Deliberately scoped to NEGATIVE values only — a large positive
// `startingTextElement`/`lengthInTextElements` can also throw, but whether it does depends on the
// specific string's text-element count, which this analyzer cannot know in general (content-dependent,
// correctly out of scope, same discipline as `InvalidRandomNumberGeneratorGetStringArgumentAnalyzer`'s
// non-literal-choices bail-out, RPAD0515). When BOTH arguments are negative, `startingTextElement` is
// checked first (confirmed via the real probe's actual `ParamName`), so the probe — not a hand-rolled
// sign check — is trusted to attribute the diagnostic to whichever parameter the real implementation
// actually flags first.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStringInfoSubstringByTextElementsArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0523";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "StringInfo.SubstringByTextElements argument is negative",
        messageFormat: "'StringInfo.SubstringByTextElements(...)' always throws ArgumentOutOfRangeException at runtime — {0} cannot be negative ({1})",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StringInfo.SubstringByTextElements throws ArgumentOutOfRangeException for a negative startingTextElement or lengthInTextElements, independent of the string the StringInfo was constructed from.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "SubstringByTextElements" })
            return;
        var arguments = invocation.ArgumentList.Arguments;
        if (arguments.Count is not (1 or 2)) return;
        var isTwoArg = arguments.Count == 2;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "SubstringByTextElements", ContainingType: { } containingType })
            return;
        if (containingType.ToDisplayString() != "System.Globalization.StringInfo") return;

        var start = context.SemanticModel.GetConstantValue(arguments[0].Expression, ct).Value as int?;
        var length = isTwoArg ? context.SemanticModel.GetConstantValue(arguments[1].Expression, ct).Value as int? : null;
        if (start is not < 0 && length is not < 0) return;

        var probe = new StringInfo("probe text");
        try
        {
            if (isTwoArg) probe.SubstringByTextElements(start ?? 0, length ?? 0);
            else probe.SubstringByTextElements(start ?? 0);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            var badValue = ex.ParamName == "lengthInTextElements" ? length : start;
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), ex.ParamName, badValue));
        }
    }
}
