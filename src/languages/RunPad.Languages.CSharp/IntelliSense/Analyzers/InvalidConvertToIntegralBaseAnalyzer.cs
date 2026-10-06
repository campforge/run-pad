using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести сорок второй проход, batch) — own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeElementAtIndexAnalyzer (RPAD0236)/
// NegativeStringRepeatCountAnalyzer (RPAD0237, same batch), for a non-negative-related validation
// instead: `Convert.ToInt32(value, fromBase)` (and the SByte/Int16/Int64 overloads) are documented to
// throw `ArgumentException` unless `fromBase` is exactly 2, 8, 10, or 16 — any other literal base always
// throws, a mistake the compiler can't catch on its own since fromBase is just a normal int parameter.
// Extended in the двести девяностый проход to also cover `Convert.ToByte(string, int fromBase)` — the
// fifth and last integral overload with a `fromBase` parameter (there is no unsigned Int16/32/64 overload
// with `fromBase`), confirmed via scratch script to throw identically to the other four for an
// out-of-set base; missed in the original батч, caught via PROBE while brainstorming the next batch.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidConvertToIntegralBaseAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0238";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Convert.ToIntXX with an unsupported base always throws ArgumentException",
        messageFormat: "'Convert.{0}(..., {1})' always throws ArgumentException at runtime — fromBase must be 2, 8, 10, or 16",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "Convert.ToSByte/ToInt16/ToInt32/ToInt64(string, int fromBase) throw ArgumentException unless fromBase is exactly 2, 8, 10, or 16.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> TargetMethods = ImmutableHashSet.Create(
        "ToSByte", "ToByte", "ToInt16", "ToInt32", "ToInt64");

    private static readonly ImmutableHashSet<int> ValidBases = ImmutableHashSet.Create(2, 8, 10, 16);

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
        if (!TargetMethods.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var baseArg }]) return;
        if (baseArg is not LiteralExpressionSyntax { RawKind: (int)SyntaxKind.NumericLiteralExpression } baseLiteral)
            return;
        if (baseLiteral.Token.Value is not int baseValue) return;
        if (ValidBases.Contains(baseValue)) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: var resolvedName } method
            || resolvedName != methodName)
        {
            return;
        }
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), methodName, baseValue));
    }
}
