using System.Collections.Immutable;
using System.Diagnostics.Tracing;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Diagnostics.Tracing.EventSourceOptions.Level`
// (property type `EventLevel`, name differs from type name) and `.Opcode` (property type `EventOpcode`,
// also differs) — both `int`-backed enums (confirmed via `Enum.GetUnderlyingType`, not assumed, per this
// session's own enum-underlying-type lesson). Confirmed empirically an unusual exception type for this
// catalog: `System.OverflowException` ("Arithmetic operation resulted in an overflow"), not the
// `ArgumentOutOfRangeException`/`InvalidEnumArgumentException` families used elsewhere — both fields are
// packed into a private `byte` internally, so an out-of-[0,255]-range value overflows the checked cast.
// Confirmed the boundary is exactly [0, 255] by exhaustive sweep (0/4/100/255 valid; -1/256/1000/
// int.MaxValue throw). Confirmed `EventSourceOptions` is a plain value-type struct with no shared/static
// state — mutating one instance leaves a separately-constructed instance untouched. Assignment alone
// (never calling `EventSource.Write`) triggers the throw, so no ETW session is ever created — no side
// effects. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidEventSourceOptionsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0460";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "EventSourceOptions property assignment always throws",
        messageFormat: "'EventSourceOptions.{0} = {1}' always throws OverflowException at runtime — valid values are 0-255",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "EventSourceOptions.Level and .Opcode throw OverflowException for a value outside 0-255 (both are packed internally into a private byte field).");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Level" or "Opcode" } access)
            return;
        var propertyName = access.Name.Identifier.Text;
        if (assignment.Right is not CastExpressionSyntax { Expression: var innerExpr }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Diagnostics.Tracing.EventSourceOptions") return;

        var enumTypeName = context.SemanticModel.GetTypeInfo(assignment.Right, ct).Type?.ToDisplayString();
        var expectedEnumType = propertyName == "Level" ? "System.Diagnostics.Tracing.EventLevel" : "System.Diagnostics.Tracing.EventOpcode";
        if (enumTypeName != expectedEnumType) return;

        var options = new EventSourceOptions();
        try
        {
            if (propertyName == "Level") options.Level = (EventLevel)rawValue;
            else options.Opcode = (EventOpcode)rawValue;
        }
        catch (System.OverflowException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, rawValue));
        }
    }
}
