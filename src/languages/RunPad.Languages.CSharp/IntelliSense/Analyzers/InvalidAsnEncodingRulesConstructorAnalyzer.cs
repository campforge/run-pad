using System.Collections.Immutable;
using System.Formats.Asn1;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #7 from the nineteenth research subagent survey,
// re-verified independently before implementing. `System.Formats.Asn1.AsnWriter(AsnEncodingRules)` and
// `AsnReader(ReadOnlyMemory<byte>, AsnEncodingRules, ...)` constructors — an out-of-range
// `AsnEncodingRules` value (defined members `BER`=0/`CER`=1/`DER`=2 only) throws
// `ArgumentOutOfRangeException` (param `ruleSet`, matching the enum's own type name). `AsnEncodingRules`
// underlying type is `int` (confirmed via `Enum.GetUnderlyingType`). Confirmed no
// `[SupportedOSPlatform]`/`[UnsupportedOSPlatform]` gating on either type. Confirmed no side effects —
// `AsnReader`'s constructor never parses the input bytes, and `AsnWriter`'s never writes anything, both
// purely validate the rule-set argument. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidAsnEncodingRulesConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0467";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "AsnWriter/AsnReader ruleSet argument is out of range",
        messageFormat: "'new {0}(...)' always throws ArgumentOutOfRangeException at runtime — {1} is not a defined AsnEncodingRules value",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "AsnWriter(AsnEncodingRules)/AsnReader(ReadOnlyMemory<byte>, AsnEncodingRules, ...) throw ArgumentOutOfRangeException for a value outside BER/CER/DER (0-2).");

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
        var typeName = containingType.ToDisplayString();
        var shortName = typeName switch
        {
            "System.Formats.Asn1.AsnWriter" => "AsnWriter",
            "System.Formats.Asn1.AsnReader" => "AsnReader",
            _ => null,
        };
        if (shortName is null) return;

        var ruleSetIndex = -1;
        for (var i = 0; i < ctor.Parameters.Length; i++)
        {
            if (ctor.Parameters[i].Type.ToDisplayString() != "System.Formats.Asn1.AsnEncodingRules") continue;
            ruleSetIndex = i;
            break;
        }
        if (ruleSetIndex < 0) return;
        if (creation.ArgumentList is not { } argumentList || argumentList.Arguments.Count <= ruleSetIndex) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(argumentList.Arguments[ruleSetIndex].Expression, ct).Value is not int rawValue) return;

        try
        {
            if (shortName == "AsnWriter") _ = new AsnWriter((AsnEncodingRules)rawValue);
            else _ = new AsnReader(new byte[] { 0 }, (AsnEncodingRules)rawValue);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), shortName, (AsnEncodingRules)rawValue));
        }
    }
}
