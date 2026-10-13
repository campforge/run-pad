using System.Collections.Immutable;
using System.Text.Unicode;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #4 from the twenty-second research subagent survey,
// re-verified independently before implementing. `System.Text.Unicode.UnicodeRange(int
// firstCodePoint, int length)` — used by `HtmlEncoder.Create`/`JavaScriptEncoder.Create`/
// `UrlEncoder.Create`'s `CreateRange` overloads; the validation lives entirely in the struct
// constructor itself (confirmed the encoder factory methods add no extra validation of their own —
// dead end explored and declined). Two independent literal-only checks, both confirmed via boundary
// testing: `firstCodePoint` must be in `[0, 0xFFFF]`; `length` must be non-negative AND
// `firstCodePoint + length <= 0x10000` — a genuine two-joint-literal check (same shape precedent as
// `InvalidPipeOptionsConstructorAnalyzer`, RPAD0454), but unlike a schema/state-dependent index check,
// both operands here are the literal arguments themselves, not external collection state. Confirmed no
// `[SupportedOSPlatform]` gating (reflection: no attributes on the type at all) and no side effects —
// pure struct constructor. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP (pure numeric checks).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidUnicodeRangeConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0478";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "UnicodeRange constructor argument is out of range",
        messageFormat: "'new UnicodeRange({0}, {1})' always throws ArgumentOutOfRangeException at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "UnicodeRange(int firstCodePoint, int length) throws when firstCodePoint is outside [0, 0xFFFF], when length is negative, or when firstCodePoint + length exceeds 0x10000.");

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
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Text.Unicode.UnicodeRange") return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var firstArg }, { Expression: var lengthArg }] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(firstArg, ct).Value is not int firstCodePoint) return;
        if (context.SemanticModel.GetConstantValue(lengthArg, ct).Value is not int length) return;

        try
        {
            _ = new UnicodeRange(firstCodePoint, length);
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            var reason = ex.ParamName == "firstCodePoint"
                ? "firstCodePoint must be between 0 and 0xFFFF"
                : "length must be non-negative and firstCodePoint + length must not exceed 0x10000";
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), firstCodePoint, length, reason));
        }
    }
}
