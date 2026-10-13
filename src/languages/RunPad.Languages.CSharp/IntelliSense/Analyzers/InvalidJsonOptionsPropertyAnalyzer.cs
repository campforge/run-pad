using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Candidates #1/#5 from the fifteenth research subagent survey,
// re-verified independently before implementing. `JsonReaderOptions`/`JsonWriterOptions`/
// `JsonDocumentOptions` — siblings of the already-covered `JsonSerializerOptions` (RPAD0443), sharing the
// same `MaxDepth` property (negative throws `ArgumentOutOfRangeException`, 0 is valid) plus a
// `CommentHandling` property on the two reader-facing types. The standout finding: `JsonCommentHandling.
// Allow` (value 2) is a LEGALLY DEFINED enum member, valid on `JsonReaderOptions.CommentHandling`
// (confirmed no-throw) — but `JsonDocumentOptions.CommentHandling` rejects it too ("Comments cannot be
// stored in a JsonDocument, only the Skip and Disallow comment handling modes are supported"), a
// genuinely different, narrower valid set than a plain enum-range check would suggest. Confirmed
// empirically rather than assumed: `JsonWriterOptions` has no `CommentHandling` property at all (grep +
// reflection), so this analyzer only checks that property on the two types that actually have it.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects — all three types are pure structs,
// property assignment never touches any actual JSON document/stream.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidJsonOptionsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0453";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Json options property assignment is invalid",
        messageFormat: "'{0}.{1} = ...' always throws ArgumentOutOfRangeException at runtime — {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "JsonReaderOptions/JsonWriterOptions/JsonDocumentOptions.MaxDepth throw for a negative value, and JsonReaderOptions/JsonDocumentOptions.CommentHandling throw for a value outside their own supported set (JsonDocumentOptions additionally rejects the otherwise-valid JsonCommentHandling.Allow).");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "MaxDepth" or "CommentHandling" } access)
            return;
        var propertyName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        // JsonCommentHandling's underlying type is `byte`, not `int` — GetConstantValue returns the
        // constant boxed as its OWN declared type (byte for an enum member/cast, int for MaxDepth's
        // plain int literal), so a naive `is not int` pattern silently misses every CommentHandling
        // case. Caught via PROBE before shipping (AD0001-free but zero diagnostics — traced with
        // temporary debug output to find GetConstantValue itself was the bail point, then confirmed the
        // boxed runtime type was System.Byte, not System.Int32). ToInt32Constant normalizes any
        // integral-underlying-type constant to int.
        if (ToInt32Constant(context.SemanticModel.GetConstantValue(assignment.Right, ct)) is not { } rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();

        var shortTypeName = typeName switch
        {
            "System.Text.Json.JsonReaderOptions" => "JsonReaderOptions",
            "System.Text.Json.JsonWriterOptions" => "JsonWriterOptions",
            "System.Text.Json.JsonDocumentOptions" => "JsonDocumentOptions",
            _ => null,
        };
        if (shortTypeName is null) return;
        if (propertyName == "CommentHandling" && shortTypeName == "JsonWriterOptions") return;

        if (!AlwaysThrows(shortTypeName, propertyName, rawValue, out var reason)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), shortTypeName, propertyName, reason));
    }

    private static int? ToInt32Constant(Optional<object?> constant) => constant.Value switch
    {
        int i => i,
        byte b => b,
        sbyte sb => sb,
        short s => s,
        ushort us => us,
        uint u => unchecked((int)u),
        _ => null,
    };

    private static bool AlwaysThrows(string typeName, string propertyName, int rawValue, out string reason)
    {
        reason = propertyName == "MaxDepth" ? "MaxDepth must be non-negative" : "value is outside the supported CommentHandling set";
        try
        {
            switch (typeName, propertyName)
            {
                case ("JsonReaderOptions", "MaxDepth"):
                    { var o = new JsonReaderOptions(); o.MaxDepth = rawValue; break; }
                case ("JsonReaderOptions", "CommentHandling"):
                    { var o = new JsonReaderOptions(); o.CommentHandling = (JsonCommentHandling)rawValue; break; }
                case ("JsonWriterOptions", "MaxDepth"):
                    { var o = new JsonWriterOptions(); o.MaxDepth = rawValue; break; }
                case ("JsonDocumentOptions", "MaxDepth"):
                    { var o = new JsonDocumentOptions(); o.MaxDepth = rawValue; break; }
                case ("JsonDocumentOptions", "CommentHandling"):
                    { var o = new JsonDocumentOptions(); o.CommentHandling = (JsonCommentHandling)rawValue; break; }
                default:
                    return false;
            }
            return false;
        }
        catch (System.ArgumentOutOfRangeException)
        {
            return true;
        }
    }
}
