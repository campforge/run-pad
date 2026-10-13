using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidate #4 from the twelfth research subagent survey, re-verified
// independently before implementing. `System.Text.Json.JsonSerializerOptions` — 4 scalar property
// setters, all confirmed on a freshly-constructed `new JsonSerializerOptions()` (no prior serialization
// use, so no "already locked/read-only" state entanglement to worry about): `MaxDepth` (negative throws
// `ArgumentOutOfRangeException`, 0 and positive are valid — 0 means "use the default", not "no depth"),
// `DefaultBufferSize` (zero or negative throws `ArgumentException`, positive is valid), `IndentSize`
// (valid range 0-127 inclusive, throws `ArgumentOutOfRangeException` outside it), `IndentCharacter` (only
// space or horizontal tab are valid, any other char throws `ArgumentOutOfRangeException`). Confirmed
// invariant across en-US/de-DE/ar-SA/ja-JP — pure structural/range checks, no culture data involved. No
// side effects (no serialization ever performed, just property assignment on a throwaway instance).
// Extended (454-й проход, candidate #4/#5 from the sixteenth research subagent survey) to also cover
// `NumberHandling` (`JsonNumberHandling`, a `[Flags]` int-backed enum — valid range is any bit
// combination 0-7; confirmed empirically both the enum type AND its underlying type before trusting a
// naive `is int` extraction, per the enum-underlying-type lesson from the immediately-preceding проход)
// and `PreferredObjectCreationHandling` (`JsonObjectCreationHandling`, NOT `[Flags]`, also int-backed,
// valid values `Replace`(0)/`Populate`(1)) — both throw `ArgumentOutOfRangeException` for a value outside
// their own valid set, confirmed on a freshly-constructed, never-used options instance.
// Extended (459-й проход, candidate #3 from the twenty-second research subagent survey) to also cover
// `NewLine` (`string`) — throws `ArgumentOutOfRangeException` unless the value is exactly "\n" or
// "\r\n" (confirmed "" / " " / "\r" / "ab" all throw); a sibling gap this analyzer's own IndentSize/
// IndentCharacter checks didn't extend to when they were added.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidJsonSerializerOptionsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0443";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "JsonSerializerOptions property assignment is invalid",
        messageFormat: "'JsonSerializerOptions.{0} = {1}' always throws {2} at runtime — {3}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "JsonSerializerOptions.MaxDepth/DefaultBufferSize/IndentSize/IndentCharacter/NewLine throw for a value outside their own valid range.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableHashSet<string> PropertyNames = ImmutableHashSet.Create(
        "MaxDepth", "DefaultBufferSize", "IndentSize", "IndentCharacter",
        "NumberHandling", "PreferredObjectCreationHandling", "NewLine");

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
        if (typeName != "System.Text.Json.JsonSerializerOptions") return;

        if (!AlwaysThrows(propertyName, constant.Value, out var exceptionType, out var reason)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), propertyName, assignment.Right.ToString(), exceptionType, reason));
    }

    private static bool AlwaysThrows(string propertyName, object? value, out string exceptionType, out string reason)
    {
        exceptionType = "";
        reason = "";
        var options = new JsonSerializerOptions();
        try
        {
            switch (propertyName)
            {
                case "MaxDepth" when value is int maxDepth:
                    options.MaxDepth = maxDepth;
                    break;
                case "DefaultBufferSize" when value is int bufferSize:
                    options.DefaultBufferSize = bufferSize;
                    break;
                case "IndentSize" when value is int indentSize:
                    options.IndentSize = indentSize;
                    break;
                case "IndentCharacter" when value is char indentChar:
                    options.IndentCharacter = indentChar;
                    break;
                case "NumberHandling" when value is int numberHandling:
                    options.NumberHandling = (System.Text.Json.Serialization.JsonNumberHandling)numberHandling;
                    break;
                case "PreferredObjectCreationHandling" when value is int creationHandling:
                    options.PreferredObjectCreationHandling = (System.Text.Json.Serialization.JsonObjectCreationHandling)creationHandling;
                    break;
                case "NewLine" when value is string newLine:
                    options.NewLine = newLine;
                    break;
                default:
                    return false;
            }
            return false;
        }
        catch (System.Exception ex) when (ex is System.ArgumentOutOfRangeException or System.ArgumentException)
        {
            exceptionType = ex.GetType().Name;
            reason = propertyName switch
            {
                "MaxDepth" => "MaxDepth must be non-negative",
                "DefaultBufferSize" => "DefaultBufferSize must be greater than zero",
                "IndentSize" => "IndentSize must be between 0 and 127",
                "IndentCharacter" => "IndentCharacter must be a space or horizontal tab",
                "NumberHandling" => "NumberHandling must be a valid JsonNumberHandling flags combination (0-7)",
                "PreferredObjectCreationHandling" => "PreferredObjectCreationHandling must be a defined JsonObjectCreationHandling value",
                "NewLine" => "NewLine must be exactly \"\\n\" or \"\\r\\n\"",
                _ => "value is out of range",
            };
            return true;
        }
    }
}
