using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 160, batch) — own pick, not a Roslynator port. Same "redundant
// identity conversion" theme as RedundantCastAnalyzer (RPAD0022)/RedundantAsOperatorAnalyzer
// (RPAD0023), but for System.Convert's ToXxx family: `Convert.ToInt32(intValue)` just returns
// intValue unchanged — a genuine no-op, not merely a style nit, since Convert.ToInt32(int) is
// documented to return its argument as-is. Deliberately excludes ToString/ToDateTime — Convert.ToString
// turns null into "" (not a true no-op for a nullable string), and there's no meaningful "identity"
// input type for ToDateTime.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RedundantIdentityConvertCallAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0065";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Redundant identity Convert call",
        messageFormat: "'Convert.{0}' is a no-op here — the argument is already the target type",
        category: "Redundancy",
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "Convert.ToXxx(value) returns value unchanged when it's called with a value that's already of the target type.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, SpecialType> TargetTypeByMethodName = ImmutableDictionary.CreateRange(
    [
        new KeyValuePair<string, SpecialType>("ToBoolean", SpecialType.System_Boolean),
        new KeyValuePair<string, SpecialType>("ToByte", SpecialType.System_Byte),
        new KeyValuePair<string, SpecialType>("ToChar", SpecialType.System_Char),
        new KeyValuePair<string, SpecialType>("ToDecimal", SpecialType.System_Decimal),
        new KeyValuePair<string, SpecialType>("ToDouble", SpecialType.System_Double),
        new KeyValuePair<string, SpecialType>("ToInt16", SpecialType.System_Int16),
        new KeyValuePair<string, SpecialType>("ToInt32", SpecialType.System_Int32),
        new KeyValuePair<string, SpecialType>("ToInt64", SpecialType.System_Int64),
        new KeyValuePair<string, SpecialType>("ToSByte", SpecialType.System_SByte),
        new KeyValuePair<string, SpecialType>("ToSingle", SpecialType.System_Single),
        new KeyValuePair<string, SpecialType>("ToUInt16", SpecialType.System_UInt16),
        new KeyValuePair<string, SpecialType>("ToUInt32", SpecialType.System_UInt32),
        new KeyValuePair<string, SpecialType>("ToUInt64", SpecialType.System_UInt64),
    ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        if (!TargetTypeByMethodName.TryGetValue(methodName, out var targetType)) return;
        if (invocation.ArgumentList.Arguments is not [var soleArg]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;

        var argType = context.SemanticModel.GetTypeInfo(soleArg.Expression, ct).Type;
        if (argType?.SpecialType != targetType) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName));
    }
}
