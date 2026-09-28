using System.Collections.Immutable;
using System.Threading.Tasks.Dataflow;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (455-й проход, batch) — candidate #3 from the seventeenth research subagent survey,
// re-verified independently before implementing. `System.Threading.Tasks.Dataflow.DataflowLinkOptions.
// MaxMessages` (plain `int`, declared directly on `DataflowLinkOptions`, a type distinct from the
// already-covered `DataflowBlockOptions`/`ExecutionDataflowBlockOptions`, RPAD0457) — throws
// `ArgumentOutOfRangeException` for `0` or any value < -1; `-1` (unlimited) and positive values are
// valid. Same boundary shape as RPAD0457, but a genuinely different declaring type, so not already
// covered by it. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects — property
// assignment never links any dataflow blocks or schedules any work.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidDataflowLinkOptionsMaxMessagesAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0462";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "DataflowLinkOptions.MaxMessages assignment always throws",
        messageFormat: "'DataflowLinkOptions.MaxMessages = {0}' always throws ArgumentOutOfRangeException at runtime — must be -1 (unlimited) or a positive value",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "DataflowLinkOptions.MaxMessages throws ArgumentOutOfRangeException for 0 or any value less than -1.");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "MaxMessages" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int value) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "MaxMessages" })
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Threading.Tasks.Dataflow.DataflowLinkOptions") return;

        try
        {
            new DataflowLinkOptions().MaxMessages = value;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), value));
        }
    }
}
