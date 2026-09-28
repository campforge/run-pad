using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести восемьдесят шестой проход, batch) — own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidGuidLiteralFormatAnalyzer/InvalidIPAddressLiteralAnalyzer
// (RPAD0246, RPAD0249), applied to an ENUM argument instead of a string: `new Regex(pattern, options)`
// throws ArgumentOutOfRangeException when `options` combines `RegexOptions.ECMAScript` or
// `RegexOptions.NonBacktracking` with other flags outside their small documented-compatible subsets
// (confirmed empirically before writing this — e.g. `ECMAScript | RightToLeft` throws, but `ECMAScript |
// IgnoreCase` doesn't). Rather than hand-encoding the exact compatible-flag lists, this analyzer calls
// the REAL `Regex` constructor in-process on a trivial, always-valid pattern (`"."`) with the literal
// `options` value inside a try/catch — the pattern argument itself is irrelevant to this exception (it
// comes from the options value alone), so a placeholder pattern isolates exactly the concern this
// analyzer cares about, and the check can't drift from the real implementation's actual compatible-flag
// rules. Only proceeds when `options` is itself a compile-time constant (works for OR'd flag
// combinations, not just single values).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRegexOptionsCombinationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0274";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Regex options combination always throws",
        messageFormat: "'new Regex(...)' always throws ArgumentOutOfRangeException at runtime — this RegexOptions combination is not valid together",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "RegexOptions.ECMAScript and RegexOptions.NonBacktracking are each only compatible with a small subset of the other flags; combining them outside that subset throws ArgumentOutOfRangeException.");

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
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var optionsArg }, ..] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(optionsArg, ct).Value is not int optionsValue) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        if (ctor.ContainingType?.ToDisplayString() != "System.Text.RegularExpressions.Regex") return;
        if (ctor.Parameters is not [_, { Type.Name: "RegexOptions" }, ..]) return;

        if (!AlwaysThrows((RegexOptions)optionsValue)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
    }

    private static bool AlwaysThrows(RegexOptions options)
    {
        try
        {
            _ = new Regex(".", options);
            return false;
        }
        catch (ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
