using System.Collections.Immutable;
using System.Reflection.Metadata.Ecma335;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #1 from the twenty-third research subagent survey,
// re-verified independently before implementing. `System.Reflection.Metadata.Ecma335.
// MetadataBuilder(int userStringHeapStartOffset, int stringHeapStartOffset, int blobHeapStartOffset,
// int guidHeapStartOffset)` — first sibling found under the already-covered `BlobBuilder`'s namespace
// (RPAD0474). Confirmed empirically: any of the first three offsets negative throws
// `ArgumentOutOfRangeException` (per-parameter name); `guidHeapStartOffset` negative also throws
// `ArgumentOutOfRangeException`, but a non-negative value that isn't a multiple of 16 throws a
// DIFFERENT exception, `ArgumentException` ("Value must be multiple of 16") — confirmed boundary: 16
// is valid, 1/17/int.MaxValue all throw. Confirmed no `[SupportedOSPlatform]` gating (reflection: only
// nullable-context attributes) and no side effects — pure in-memory metadata-table builder
// construction. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP (pure numeric checks).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidMetadataBuilderConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0479";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "MetadataBuilder constructor argument always throws",
        messageFormat: "'new MetadataBuilder(...)' always throws {0} at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "MetadataBuilder throws ArgumentOutOfRangeException for a negative heap-start offset, and ArgumentException when guidHeapStartOffset isn't a multiple of 16.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly string[] ParameterNames =
        ["userStringHeapStartOffset", "stringHeapStartOffset", "blobHeapStartOffset", "guidHeapStartOffset"];

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
                Parameters: [{ Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Reflection.Metadata.Ecma335.MetadataBuilder") return;
        if (creation.ArgumentList is not { Arguments: [{ Expression: var e0 }, { Expression: var e1 }, { Expression: var e2 }, { Expression: var e3 }] })
            return;

        var ct = context.CancellationToken;
        var values = new int?[4];
        if (context.SemanticModel.GetConstantValue(e0, ct).Value is int v0) values[0] = v0;
        if (context.SemanticModel.GetConstantValue(e1, ct).Value is int v1) values[1] = v1;
        if (context.SemanticModel.GetConstantValue(e2, ct).Value is int v2) values[2] = v2;
        if (context.SemanticModel.GetConstantValue(e3, ct).Value is int v3) values[3] = v3;

        // Fill in unresolved (non-constant) arguments with a known-valid placeholder so we can still
        // probe the ones that ARE literal — 0 is valid for the first three, 0 is valid for the fourth.
        var probe = new int[4];
        for (var i = 0; i < 4; i++) probe[i] = values[i] ?? 0;
        if (values[0] is null && values[1] is null && values[2] is null && values[3] is null) return;

        try
        {
            _ = new MetadataBuilder(probe[0], probe[1], probe[2], probe[3]);
        }
        catch (System.ArgumentOutOfRangeException ex)
        {
            var index = System.Array.IndexOf(ParameterNames, ex.ParamName);
            if (index < 0 || values[index] is null) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "ArgumentOutOfRangeException", $"{ParameterNames[index]} cannot be negative"));
        }
        catch (System.ArgumentException ex) when (ex.ParamName == "guidHeapStartOffset")
        {
            if (values[3] is null) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), "ArgumentException", "guidHeapStartOffset must be a multiple of 16"));
        }
    }
}
