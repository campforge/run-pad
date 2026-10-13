using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Text.EncoderReplacementFallback(string)`/
// `System.Text.DecoderReplacementFallback(string)` — first touch of the fallback-replacement classes.
// Confirmed empirically: both throw `ArgumentException` ("String contains invalid Unicode code
// points") whenever the string contains ANY unpaired UTF-16 surrogate (lone high surrogate, lone low
// surrogate, wrong-order pair, or a surrogate embedded mid-string — all confirmed to throw); a valid
// surrogate pair, an ordinary string, and the empty string are all valid. Explicitly checked
// environment-dependence (per the lesson from `InvalidStringNormalizeLiteralAnalyzer`/RPAD0422, whose
// underlying API stops throwing under `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`) — re-ran both
// constructors under that env var and confirmed they still throw identically, no caveat needed. No
// `[SupportedOSPlatform]` gating (reflection: only nullable-context attributes), no side effects —
// pure managed construction. This is structural UTF-16 surrogate-pairing validation, not
// locale-dependent, so it's culture-irrelevant despite superficially resembling
// `InvalidRuneOrSurrogatePairConstructorArgumentAnalyzer`/`InvalidUnicodeCodePointArgumentAnalyzer`
// (different receiver types/APIs, confirmed via reading their doc comments before implementing).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidReplacementFallbackConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0495";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Replacement fallback string contains an unpaired surrogate",
        messageFormat: "'new {0}(...)' always throws ArgumentException at runtime — the replacement string contains an unpaired UTF-16 surrogate",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "EncoderReplacementFallback(string)/DecoderReplacementFallback(string) throw ArgumentException when the replacement string contains an unpaired UTF-16 surrogate.");

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
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
            })
        {
            return;
        }
        var typeName = containingType.ToDisplayString();
        var shortName = typeName switch
        {
            "System.Text.EncoderReplacementFallback" => "EncoderReplacementFallback",
            "System.Text.DecoderReplacementFallback" => "DecoderReplacementFallback",
            _ => null,
        };
        if (shortName is null) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var replacementArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(replacementArg, ct).Value is not string replacement) return;

        try
        {
            if (shortName == "EncoderReplacementFallback") _ = new EncoderReplacementFallback(replacement);
            else _ = new DecoderReplacementFallback(replacement);
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), shortName));
        }
    }
}
