using System.Collections.Immutable;
using System.Reflection.PortableExecutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (461-й проход, batch) — candidate C from the twenty-fourth research subagent survey,
// re-verified independently before implementing. `System.Reflection.PortableExecutable.
// PEHeaderBuilder(...)` — a sibling under the same assembly/namespace tree as the already-covered
// `MetadataBuilder`/`InstructionEncoder` (RPAD0479-0481). Both `fileAlignment` and `sectionAlignment`
// are optional named `int` parameters (positions 1/2). Confirmed empirically: `fileAlignment` must be a
// power of two in `[512, 65536]`, checked INDEPENDENTLY of `sectionAlignment` (confirmed 513 throws
// even with a huge sectionAlignment; 512/65536 are the exact valid boundary values; 511/0/negative/
// 131072 all throw). `sectionAlignment` must ALSO be a power of two AND `>= fileAlignment` (confirmed
// 300 throws as non-power-of-two; a smaller sectionAlignment than fileAlignment throws; equal values
// are valid; confirmed no apparent upper cap on sectionAlignment beyond fileAlignment's own bound, so
// this analyzer only flags sectionAlignment for non-power-of-two or less-than-fileAlignment, never an
// upper bound). All other 17 parameters (imageBase, version fields, size-of-reserve/commit, etc.)
// confirmed completely unchecked — declined, not included. No `[SupportedOSPlatform]` gating, no side
// effects — pure in-memory header-value bookkeeping, no PE file ever written. Confirmed invariant
// across en-US/de-DE/ar-SA/ja-JP (pure numeric/bitwise checks).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidPEHeaderBuilderConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0486";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "PEHeaderBuilder alignment argument is invalid",
        messageFormat: "'new PEHeaderBuilder(...)' always throws ArgumentOutOfRangeException at runtime — {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PEHeaderBuilder throws ArgumentOutOfRangeException when fileAlignment isn't a power of two in [512, 65536], or when sectionAlignment isn't a power of two that is at least fileAlignment.");

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
        if (containingType.ToDisplayString() != "System.Reflection.PortableExecutable.PEHeaderBuilder") return;
        if (creation.ArgumentList is not { } argumentList) return;

        var ct = context.CancellationToken;
        int? sectionAlignment = null;
        int? fileAlignment = null;
        for (var i = 0; i < argumentList.Arguments.Count; i++)
        {
            var arg = argumentList.Arguments[i];
            var paramName = arg.NameColon?.Name.Identifier.Text ?? (i < ctor.Parameters.Length ? ctor.Parameters[i].Name : null);
            if (paramName == "sectionAlignment" && context.SemanticModel.GetConstantValue(arg.Expression, ct).Value is int sa)
                sectionAlignment = sa;
            else if (paramName == "fileAlignment" && context.SemanticModel.GetConstantValue(arg.Expression, ct).Value is int fa)
                fileAlignment = fa;
        }
        // Defaults from the real constructor when the argument was omitted entirely.
        var probeSection = sectionAlignment ?? 8192;
        var probeFile = fileAlignment ?? 512;
        if (sectionAlignment is null && fileAlignment is null) return;

        try
        {
            _ = new PEHeaderBuilder(sectionAlignment: probeSection, fileAlignment: probeFile);
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            if (ex.ParamName == "fileAlignment")
            {
                if (fileAlignment is null) return;
                context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "fileAlignment must be a power of two between 512 and 65536"));
            }
            else if (ex.ParamName == "sectionAlignment")
            {
                if (sectionAlignment is null) return;
                context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "sectionAlignment must be a power of two that is at least fileAlignment"));
            }
        }
    }
}
