using System.Collections.Immutable;
using System.Formats.Asn1;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `AsnWriter.WriteCharacterString(UniversalTagNumber,
// string)` and `AsnWriter.WriteObjectIdentifier(string)` — bundled into one analyzer since both are
// instance-method call shapes on the same receiver type, same "several call shapes, one diagnostic"
// precedent already established by `InvalidHashAlgorithmNameLiteralAnalyzer` (RPAD0426).
// `WriteCharacterString`: only 7 of the 37 `UniversalTagNumber` members are actual string types
// (`UTF8String`/`PrintableString`/`TeletexString`/`IA5String`/`ISO646String`/`BMPString`/
// `NumericString`) — any other LEGALLY-DEFINED enum member (e.g. `Boolean`/`Integer`/`Sequence`) throws
// `ArgumentOutOfRangeException` (param `encodingType`, differs from the type name `UniversalTagNumber`)
// regardless of the string content — a richer shape than a flat invalid-range check, same class as
// `InvalidTarEntryConstructorAnalyzer` (RPAD0466). `UniversalTagNumber`'s underlying type is `int`.
// Deliberately excludes `NumericString` from this analyzer's positive-content-dependent path — that tag
// IS valid, but then separately validates whether the string content is actually numeric (a
// content-dependent failure this analyzer doesn't reason about, only the invariant tag-rejection case).
// `WriteObjectIdentifier(string)`: throws `ArgumentException` (param `oidValue`) for an empty string, or
// any string that isn't a dotted-decimal OID with at least 2 arcs — confirmed a single arc like `"1"`
// also throws, OIDs need ≥2 arcs. Confirmed no `[StringSyntax]` attribute on this parameter (checked via
// reflection on both the `string` and `ReadOnlySpan<char>` overloads). Confirmed no
// `[SupportedOSPlatform]` gating and no side effects on either method — writing to an in-memory
// `AsnWriter` buffer, never touches a file/network. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidAsnWriterMethodArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0468";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "AsnWriter method argument always throws",
        messageFormat: "'AsnWriter.{0}(...)' always throws {1} at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "AsnWriter.WriteCharacterString throws ArgumentOutOfRangeException for a UniversalTagNumber that isn't a string type, and AsnWriter.WriteObjectIdentifier throws ArgumentException for a string that isn't a valid dotted-decimal OID.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "WriteCharacterString" or "WriteObjectIdentifier" } access)
            return;
        var methodName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (containingType.ToDisplayString() != "System.Formats.Asn1.AsnWriter") return;
        if (method.Name != methodName) return;

        var writer = new AsnWriter(AsnEncodingRules.DER);

        if (methodName == "WriteCharacterString")
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var tagArg }, _, ..]) return;
            if (context.SemanticModel.GetConstantValue(tagArg, ct).Value is not int rawTag) return;

            try
            {
                writer.WriteCharacterString((UniversalTagNumber)rawTag, "x");
            }
            catch (System.ArgumentOutOfRangeException)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "ArgumentOutOfRangeException", $"{(UniversalTagNumber)rawTag} is not a string UniversalTagNumber"));
            }
        }
        else
        {
            if (invocation.ArgumentList.Arguments is not [{ Expression: var oidArg }, ..]) return;
            if (context.SemanticModel.GetConstantValue(oidArg, ct).Value is not string oidValue) return;

            try
            {
                writer.WriteObjectIdentifier(oidValue);
            }
            catch (System.ArgumentException)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, "ArgumentException", $"'{oidValue}' is not a valid dotted-decimal OID"));
            }
        }
    }
}
