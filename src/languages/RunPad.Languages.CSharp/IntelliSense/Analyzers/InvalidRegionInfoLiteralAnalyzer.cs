using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port; candidate
// surfaced by the same research subagent survey as RPAD0370/RPAD0371 (проход 377), confirmed empirically
// before implementing. Sibling of InvalidCultureNameLiteralAnalyzer (RPAD0248, проход 255) — same "run
// the real validator" theme and same try/catch-around-the-real-constructor technique (there's no
// `RegionInfo.TryCreate`/similar), applied to `new RegionInfo(string)` instead of `CultureInfo`:
// `RegionInfo`'s own doc comment already flagged this as a genuine gap — `InvalidCultureNameLiteralAnalyzer`
// only matches `ContainingType == "System.Globalization.CultureInfo"`, so `RegionInfo` was never in its
// scope despite the thematic closeness. Confirmed empirically that `RegionInfo(string)` throws
// `ArgumentException` (not `CultureNotFoundException`, a genuinely different exception type from its
// `CultureInfo` sibling) for a name that isn't a recognized region — accepting BOTH a two-letter ISO
// region code ("US") and a full culture name ("en-US"), case-insensitively, confirmed empirically for
// both forms before scoping the analyzer to just "does construction throw," not to a specific name shape.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidRegionInfoLiteralAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0372";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "RegionInfo name literal is not a recognized region",
        messageFormat: "'new RegionInfo(...)' always throws ArgumentException at runtime — '{0}' is not a recognized region name",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "The RegionInfo(string) constructor throws ArgumentException for a name that isn't a recognized two-letter region code or culture name.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var ct = context.CancellationToken;
        if (context.Node is not BaseObjectCreationExpressionSyntax creation) return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var nameArg }, ..] }) return;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string name) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } method)
            return;
        if (method.ContainingType?.ToDisplayString() != "System.Globalization.RegionInfo") return;

        try
        {
            _ = new RegionInfo(name);
        }
        catch (ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, context.Node.GetLocation(), name));
        }
    }
}
