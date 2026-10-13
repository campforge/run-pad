using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; second of the
// four "needs verification" candidates from the research subagent's ranked list (проход 377), confirmed
// empirically before implementing. Same "run the real validator" theme as this catalog's other analyzers,
// applied to the instance methods `IdnMapping.GetAscii(string)`/`GetUnicode(string)` instead of a
// constructor. Genuine correctness subtlety, caught BEFORE implementing (not after, unlike the
// InvalidCryptoAlgorithmKeySizeAnalyzer/RPAD0352 self-correction): `IdnMapping.AllowUnassigned`/
// `UseStd3AsciiRules` are mutable instance properties that change throw behavior for SOME inputs —
// confirmed empirically (all four flag combinations tested via scratch script) that a string with an
// underscore ("foo_bar.com") throws ArgumentException only when `UseStd3AsciiRules = true`, NOT under the
// default `false` — so naively scoping this analyzer to "receiver's static type is IdnMapping" (the same
// pattern RPAD0352 already uses for Aes/DES/TripleDES/RSA, where KeySize/BlockSize legality has no such
// configurable-flag dependency) would risk a false positive for a real receiver instance configured with
// non-default flags. Fixed by validating with the MOST PERMISSIVE flag combination (`AllowUnassigned =
// true, UseStd3AsciiRules = false`) inside the try/catch — confirmed empirically that every literal this
// analyzer actually flags (empty string, a label over 63 characters, an empty label from a doubled/
// leading/trailing separator) throws under EVERY flag combination, not just the permissive one, so
// "throws under the most permissive config" soundly implies "throws for any real receiver's config" — these
// are length/structural validity failures, not flag-gated policy rejections like the underscore case.
// Message uses the actual invocation source text (`invocation.ToString()`), matching
// InvalidJsonElementPropertyOrIndexAnalyzer's (RPAD0364) precedent for an arbitrary-receiver instance call,
// rather than a hardcoded type name — there's no single "the receiver" to name generically here the way
// RPAD0352's static `Aes`/`RSA` factory calls have.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidIdnMappingLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0376";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "IdnMapping literal is not a valid IDN label or domain name",
        messageFormat: "'{0}' always throws ArgumentException at runtime — '{1}' is not a valid IDN label or domain name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "IdnMapping.GetAscii(string)/GetUnicode(string) throw ArgumentException for a string with an empty or over-length label, regardless of AllowUnassigned/UseStd3AsciiRules.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "GetAscii" or "GetUnicode" } access)
            return;
        var methodName = access.Name.Identifier.Text;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                IsStatic: false,
                Name: var resolvedName,
                Parameters: [{ Type.SpecialType: SpecialType.System_String }],
                ContainingType: { } containingType,
            } || resolvedName != methodName)
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Globalization.IdnMapping") return;

        if (!AlwaysThrows(methodName, value)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), value));
    }

    private static bool AlwaysThrows(string methodName, string value)
    {
        var idn = new IdnMapping { AllowUnassigned = true, UseStd3AsciiRules = false };
        try
        {
            _ = methodName == "GetAscii" ? idn.GetAscii(value) : idn.GetUnicode(value);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
