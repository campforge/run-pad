using System.Collections.Immutable;
using System.Net.Http;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `SocketsHttpHandler.InitialHttp2StreamWindowSize` (int)
// — throws `ArgumentOutOfRangeException` unless the value is within `[65535, 16777216]` inclusive.
// Confirmed empirically: `0` throws, `65535` (lower boundary) is valid, `16777216` (upper boundary) is
// valid, `16777217` throws. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects. Kept as
// its own analyzer rather than folded into `InvalidHttpNonPositiveLimitPropertyAnalyzer` (RPAD0447) —
// that analyzer's boundary is a uniform "must be positive" shared across 4 (type, property) pairs, while
// this one has its own genuinely different two-sided range specific to a single property.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidSocketsHttpHandlerStreamWindowSizeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0449";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "SocketsHttpHandler.InitialHttp2StreamWindowSize is out of range",
        messageFormat: "'SocketsHttpHandler.InitialHttp2StreamWindowSize = {0}' always throws ArgumentOutOfRangeException at runtime — valid values are between 65535 and 16777216, inclusive",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "SocketsHttpHandler.InitialHttp2StreamWindowSize throws ArgumentOutOfRangeException for a value outside [65535, 16777216].");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "InitialHttp2StreamWindowSize" } access)
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(assignment.Right, ct).Value is not int value) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: "InitialHttp2StreamWindowSize" })
            return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Net.Http.SocketsHttpHandler") return;

        try
        {
            using var handler = new SocketsHttpHandler();
            handler.InitialHttp2StreamWindowSize = value;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), value));
        }
    }
}
