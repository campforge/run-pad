using System.Collections.Immutable;
using System.Reflection.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #2 from the twenty-first research subagent survey,
// re-verified independently before implementing. `System.Reflection.Metadata.BlobBuilder.
// WriteCompressedInteger(int)`/`.WriteCompressedSignedInteger(int)` — ECMA-335 §II.23.2 compressed-
// integer encodings, fixed valid ranges independent of any other state. Confirmed empirically:
// `WriteCompressedInteger` valid range is [0, 0x1FFFFFFF] (536,870,911); `WriteCompressedSignedInteger`
// valid range is [-268435456, 268435455] (i.e. [-2^28, 2^28-1]). Both throw
// `ArgumentOutOfRangeException` (param `value`) outside their range. First analyzer in the catalog to
// touch `System.Reflection.Metadata`/ECMA-335 metadata-emission APIs. Confirmed no
// `[SupportedOSPlatform]` gating (reflection: only nullable-context/DebuggerDisplay attributes present)
// and no side effects — `BlobBuilder` is a pure growable in-memory byte buffer, no OS resource; the
// exception fires before any write. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP (both are
// pure-numeric range checks, no culture-formatted input involved).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBlobBuilderCompressedIntegerAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0474";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BlobBuilder compressed-integer argument is out of range",
        messageFormat: "'BlobBuilder.{0}({1})' always throws ArgumentOutOfRangeException at runtime — the value is outside the valid ECMA-335 compressed-integer range",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BlobBuilder.WriteCompressedInteger throws for a value outside [0, 0x1FFFFFFF]; WriteCompressedSignedInteger throws for a value outside [-2^28, 2^28-1].");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName } access)
            return;
        var isSigned = methodName == "WriteCompressedSignedInteger";
        if (!isSigned && methodName != "WriteCompressedInteger") return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { ContainingType: { } containingType } method)
            return;
        if (method.Name != methodName) return;
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.BlobBuilder") return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var firstArg }]) return;
        if (context.SemanticModel.GetConstantValue(firstArg, ct).Value is not int value) return;

        var builder = new BlobBuilder();
        try
        {
            if (isSigned) builder.WriteCompressedSignedInteger(value);
            else builder.WriteCompressedInteger(value);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, value));
        }
    }
}
