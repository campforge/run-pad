using System.Collections.Immutable;
using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Diagnostics.TraceSwitch.Level` — property type
// `TraceLevel` (name differs from type name), `int`-backed (confirmed via `Enum.GetUnderlyingType`).
// Confirmed empirically: throws `ArgumentException` ("The Level must be set to a value in the
// enumeration TraceLevel.") for any value outside 0-4 (`Off`/`Error`/`Warning`/`Info`/`Verbose`);
// confirmed the in-range boundaries (0 and 4) don't throw. Confirmed invariant across en-US/de-DE/ar-SA/
// ja-JP. Confirmed no cross-instance state leakage — two independently-constructed `TraceSwitch`
// instances (even sharing the same display name) don't share a mutated `Level`. `TraceSwitch`'s base
// `Switch` class is known from .NET Framework to optionally read config-file initial values and register
// itself in an internal weak-referenced list for config-reload support — confirmed this registration
// uses `WeakReference` (GC-eligible, not a durable global-state mutation) and manifested no observable
// side effect in testing on .NET (no config file present); milder than the declined `ServicePointManager`
// candidate, which mutates ambient process behavior directly.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTraceSwitchLevelAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0461";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TraceSwitch.Level assignment always throws",
        messageFormat: "'TraceSwitch.Level = {0}' always throws ArgumentException at runtime — valid values are 0-4 (Off/Error/Warning/Info/Verbose)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TraceSwitch.Level throws ArgumentException for a TraceLevel value outside its own 0-4 defined range.");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Level" } access)
            return;
        if (assignment.Right is not CastExpressionSyntax { Expression: var innerExpr }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(innerExpr, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "Level" })
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Diagnostics.TraceSwitch") return;

        var enumTypeName = context.SemanticModel.GetTypeInfo(assignment.Right, ct).Type?.ToDisplayString();
        if (enumTypeName != "System.Diagnostics.TraceLevel") return;

        try
        {
            new TraceSwitch("RPAD0461Probe", "d").Level = (TraceLevel)rawValue;
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), rawValue));
        }
    }
}
