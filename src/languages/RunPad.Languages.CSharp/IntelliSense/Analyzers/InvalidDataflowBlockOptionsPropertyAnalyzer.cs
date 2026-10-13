using System.Collections.Immutable;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidates #1/#2 from the sixteenth research subagent survey,
// re-verified independently before implementing. `System.Threading.Tasks.Dataflow.DataflowBlockOptions.
// BoundedCapacity` (declared on the base type, inherited by `ExecutionDataflowBlockOptions`/
// `GroupingDataflowBlockOptions`) and `ExecutionDataflowBlockOptions.MaxDegreeOfParallelism` (declared
// only on that derived type, not the base) — first analyzer in this catalog touching
// `System.Threading.Tasks.Dataflow` (confirmed present in the SDK's own `Microsoft.NETCore.App.Ref`
// pack, no extra `PackageReference` needed, despite also existing as a standalone NuGet package).
// Confirmed empirically, both throw `ArgumentOutOfRangeException` for `0` or any value < -1; `-1`
// (unbounded/unlimited) and positive values are valid — same boundary shape already established for
// `ParallelOptions.MaxDegreeOfParallelism` (RPAD0455), a thematically related but syntactically distinct
// type. Confirmed `DataflowBlockOptions` itself is directly instantiable (not abstract), so
// `BoundedCapacity` is probed on the base type directly rather than needing a derived-type instance.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects — assigning these properties never
// schedules any dataflow-block work; confirmed a freshly-configured block's `Completion` task stays
// incomplete with no `Post`/`Complete` call, same "valid input must not schedule real background work"
// caution already applied to `Task.ContinueWith` (RPAD0452).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDataflowBlockOptionsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0457";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Dataflow block options property assignment always throws",
        messageFormat: "'{0}.{1} = {2}' always throws ArgumentOutOfRangeException at runtime — must be -1 (unbounded/unlimited) or a positive value",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DataflowBlockOptions.BoundedCapacity and ExecutionDataflowBlockOptions.MaxDegreeOfParallelism throw ArgumentOutOfRangeException for 0 or any value less than -1.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "BoundedCapacity" or "MaxDegreeOfParallelism" } access)
            return;
        var propertyName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int value) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName, ContainingType: { } declaringType })
            return;
        if (symbolName != propertyName) return;
        var declaringTypeName = declaringType.ToDisplayString();

        var isMatch = propertyName switch
        {
            "BoundedCapacity" => declaringTypeName == "System.Threading.Tasks.Dataflow.DataflowBlockOptions",
            "MaxDegreeOfParallelism" => declaringTypeName == "System.Threading.Tasks.Dataflow.ExecutionDataflowBlockOptions",
            _ => false,
        };
        if (!isMatch) return;

        var receiverTypeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.Name ?? declaringType.Name;

        try
        {
            if (propertyName == "BoundedCapacity")
                new DataflowBlockOptions().BoundedCapacity = value;
            else
                new ExecutionDataflowBlockOptions().MaxDegreeOfParallelism = value;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), receiverTypeName, propertyName, value));
        }
    }
}
