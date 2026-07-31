using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (pass 153, batch) — a real maintenance hazard (own pick, not a
// Roslynator port): a `switch` over an enum with no `default:` and case labels that don't cover
// every declared member silently does nothing for the missing ones today, and silently does nothing
// for any NEW member added to the enum later — the classic "forgot to update the switch" bug.
// Deliberately conservative: bails out entirely (no analysis at all, rather than guessing) if any
// section uses pattern-matching case labels (exhaustiveness for those is a much harder problem), and
// skips [Flags] enums (switching on individual flag combinations rather than covering every named
// member is a normal, different usage pattern for those).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NonExhaustiveEnumSwitchAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0048";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Switch over an enum does not handle every member",
        messageFormat: "This switch has no 'default' and does not handle: {0} — adding a new enum member later would silently fall through here too",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "A switch over an enum with no default and incomplete case coverage silently does nothing for unhandled values, now and for any member added later.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SwitchStatement);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var switchStatement = (SwitchStatementSyntax)context.Node;
        var ct = context.CancellationToken;

        if (context.SemanticModel.GetTypeInfo(switchStatement.Expression, ct).Type is not { TypeKind: TypeKind.Enum } enumType)
            return;
        if (enumType.GetAttributes().Any(a => a.AttributeClass?.ToDisplayString() == "System.FlagsAttribute"))
            return;

        var allLabels = switchStatement.Sections.SelectMany(s => s.Labels).ToList();
        if (allLabels.Any(l => l.IsKind(SyntaxKind.DefaultSwitchLabel))) return;
        if (allLabels.Any(l => l is CasePatternSwitchLabelSyntax)) return; // exhaustiveness too hard to judge — don't guess

        var allMembers = enumType.GetMembers().OfType<IFieldSymbol>()
            .Where(f => f.ConstantValue is not null).Select(f => f.Name).ToHashSet();

        var handled = new HashSet<string>();
        foreach (var label in allLabels)
        {
            if (label is not CaseSwitchLabelSyntax { Value: { } valueExpr }) continue;
            if (context.SemanticModel.GetSymbolInfo(valueExpr, ct).Symbol is IFieldSymbol field
                && SymbolEqualityComparer.Default.Equals(field.ContainingType, enumType))
            {
                handled.Add(field.Name);
            }
        }

        var missing = allMembers.Except(handled).ToList();
        if (missing.Count > 0)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Rule, switchStatement.SwitchKeyword.GetLocation(), string.Join(", ", missing)));
        }
    }
}
