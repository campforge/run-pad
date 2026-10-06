using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (двести восемьдесят девятый проход, batch) — own pick, not a Roslynator port. Fourth
// analyzer in the "run the real validator" flags-enum theme (RegexOptions/NumberStyles/DateTimeStyles —
// RPAD0274/RPAD0275/RPAD0276, проходы 286-288), applied to `CompareOptions`:
// `CompareInfo.Compare(a, b, CompareOptions)`/`string.Compare(a, b, CultureInfo, CompareOptions)` throw
// ArgumentException when `options` combines `CompareOptions.Ordinal` or `CompareOptions.OrdinalIgnoreCase`
// with ANY other flag (including each other) — both must always be used alone. Confirmed empirically
// (scratch `dotnet run` script, not assumed) across both call shapes and, separately, across
// `CompareInfo.IndexOf`/`IsPrefix` too — same throw/no-throw outcome for the same options value in every
// case tried, even though the exact ArgumentException message text differs by member ("CompareOption.Ordinal
// cannot be used with other options." vs "Value of flags is invalid.") — message text is irrelevant here,
// only whether it throws. This analyzer covers the two most common entry points
// (`CompareInfo.Compare`/`string.Compare`); `IndexOf`/`LastIndexOf`/`IsPrefix`/`IsSuffix` are candidates
// for a follow-up extension, same "new vs extend" split as the Array.Copy/ConstrainedCopy/BinarySearch
// family (RPAD0263). Calls the REAL `CultureInfo.InvariantCulture.CompareInfo.Compare` (not the specific
// original member — both call shapes were confirmed to throw/not-throw identically for the same options
// value, so one proxy suffices) on trivial placeholder strings, reporting only if that call itself throws
// ArgumentException.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCompareOptionsCombinationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0277";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "CompareOptions combination always throws",
        messageFormat: "'{0}(...)' always throws ArgumentException at runtime — CompareOptions.Ordinal/OrdinalIgnoreCase must be used alone, not combined with other options",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "CompareOptions.Ordinal and CompareOptions.OrdinalIgnoreCase are each only valid when used alone — combining either with any other CompareOptions flag (including each other) throws ArgumentException.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Compare" })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Compare" } method)
            return;

        string displayName;
        ExpressionSyntax optionsArg;
        if (method.ContainingType is { Name: "CompareInfo" } compareInfoType
            && compareInfoType.ContainingNamespace?.ToDisplayString() == "System.Globalization"
            && method.Parameters is [_, _, { Type.Name: "CompareOptions" }]
            && invocation.ArgumentList.Arguments is [_, _, { Expression: var ciOptionsArg }])
        {
            displayName = "CompareInfo.Compare";
            optionsArg = ciOptionsArg;
        }
        else if (method.ContainingType?.SpecialType == SpecialType.System_String
            && method.Parameters is [_, _, { Type.Name: "CultureInfo" }, { Type.Name: "CompareOptions" }]
            && invocation.ArgumentList.Arguments is [_, _, _, { Expression: var strOptionsArg }])
        {
            displayName = "string.Compare";
            optionsArg = strOptionsArg;
        }
        else
        {
            return;
        }

        if (context.SemanticModel.GetConstantValue(optionsArg, ct).Value is not int optionsValue) return;
        if (!AlwaysThrows((CompareOptions)optionsValue)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), displayName));
    }

    private static bool AlwaysThrows(CompareOptions options)
    {
        try
        {
            CultureInfo.InvariantCulture.CompareInfo.Compare("a", "b", options);
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }
}
