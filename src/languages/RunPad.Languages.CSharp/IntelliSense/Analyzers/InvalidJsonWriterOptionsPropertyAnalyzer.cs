using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (459-й проход, batch) — candidate #2 from the twenty-second research subagent survey,
// re-verified independently before implementing. `System.Text.Json.JsonWriterOptions.IndentSize`/
// `.IndentCharacter`/`.NewLine` — a sibling gap of the already-covered `JsonSerializerOptions.
// IndentSize`/`.IndentCharacter`/`.NewLine` (RPAD0443, this same trio, extended for `NewLine` in this
// same проход) and `InvalidJsonOptionsPropertyAnalyzer` (RPAD0453, which covers `JsonWriterOptions.
// MaxDepth` but not these three). Confirmed empirically on a freshly-constructed, never-used
// `JsonWriterOptions`: `IndentSize` valid range `[0, 127]` (throws `ArgumentOutOfRangeException`
// outside it); `IndentCharacter` valid only for space/horizontal tab (throws for any other char);
// `NewLine` valid only for exactly "\n" or "\r\n" (throws for "", " ", "\r", "ab", etc). All three
// identical in behavior to their `JsonSerializerOptions` counterparts. Confirmed no
// `[SupportedOSPlatform]` gating and no side effects — pure struct property setters, no serialization
// ever performed. Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidJsonWriterOptionsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0476";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "JsonWriterOptions property assignment is invalid",
        messageFormat: "'JsonWriterOptions.{0} = {1}' always throws ArgumentOutOfRangeException at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "JsonWriterOptions.IndentSize/IndentCharacter/NewLine throw for a value outside their own valid range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> PropertyNames = ImmutableHashSet.Create(
        "IndentSize", "IndentCharacter", "NewLine");

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.SimpleAssignmentExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var assignment = (AssignmentExpressionSyntax)context.Node;
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: var propertyName } access)
            return;
        if (!PropertyNames.Contains(propertyName)) return;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(assignment.Right, ct);
        if (!constant.HasValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();
        if (typeName != "System.Text.Json.JsonWriterOptions") return;

        if (!AlwaysThrows(propertyName, constant.Value, out var reason)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, assignment.Right.ToString(), reason));
    }

    private static bool AlwaysThrows(string propertyName, object? value, out string reason)
    {
        reason = "";
        var options = new JsonWriterOptions();
        try
        {
            switch (propertyName)
            {
                case "IndentSize" when value is int indentSize:
                    options.IndentSize = indentSize;
                    break;
                case "IndentCharacter" when value is char indentChar:
                    options.IndentCharacter = indentChar;
                    break;
                case "NewLine" when value is string newLine:
                    options.NewLine = newLine;
                    break;
                default:
                    return false;
            }
            return false;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            reason = propertyName switch
            {
                "IndentSize" => "IndentSize must be between 0 and 127",
                "IndentCharacter" => "IndentCharacter must be a space or horizontal tab",
                _ => "NewLine must be exactly \"\\n\" or \"\\r\\n\"",
            };
            return true;
        }
    }
}
