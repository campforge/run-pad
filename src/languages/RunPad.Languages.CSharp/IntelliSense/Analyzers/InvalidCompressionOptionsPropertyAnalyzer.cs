using System.Collections.Immutable;
using System.IO.Compression;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (454-й проход, batch) — candidates #6/#7/#8 from the sixteenth research subagent survey,
// re-verified independently before implementing. `System.IO.Compression.BrotliCompressionOptions.
// Quality` (plain `int`, valid range 0-11) and `System.IO.Compression.ZLibCompressionOptions.
// CompressionLevel`/`CompressionStrategy`. Confirmed empirically: `Quality` throws
// `ArgumentOutOfRangeException` outside [0, 11]. `CompressionLevel` — despite its name, confirmed the
// property's own TYPE is a plain `int`, NOT the `System.IO.Compression.CompressionLevel` ENUM (flagging
// explicitly per the property-name-vs-type-name lesson from the immediately-preceding проход) — valid
// range [-1, 9] (`-1` is the "use default" sentinel, `0` means no compression), throws outside that.
// `CompressionStrategy` — type `ZLibCompressionStrategy`, int-backed (confirmed, not `[Flags]`), valid
// values 0-4 (`Default`/`Filtered`/`HuffmanOnly`/`RunLengthEncoding`/`Fixed`), throws outside that range.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP. No side effects — both types are pure managed
// configuration objects; property assignment never performs any actual compression or touches a stream.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCompressionOptionsPropertyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0459";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Compression options property assignment is out of range",
        messageFormat: "'{0}.{1} = {2}' always throws ArgumentOutOfRangeException at runtime — {3}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BrotliCompressionOptions.Quality (0-11), ZLibCompressionOptions.CompressionLevel (-1 to 9, a plain int despite the name), and ZLibCompressionOptions.CompressionStrategy (0-4) throw ArgumentOutOfRangeException for a value outside their own valid range.");

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
        if (assignment.Left is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Quality" or "CompressionLevel" or "CompressionStrategy" } access)
            return;
        var propertyName = access.Name.Identifier.Text;

        var ct = context.CancellationToken;
        var constant = context.SemanticModel.GetConstantValue(assignment.Right, ct);
        if (constant.Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(access, ct).Symbol is not IPropertySymbol { Name: var symbolName })
            return;
        if (symbolName != propertyName) return;
        var typeName = context.SemanticModel.GetTypeInfo(access.Expression, ct).Type?.ToDisplayString();

        var shortTypeName = (typeName, propertyName) switch
        {
            ("System.IO.Compression.BrotliCompressionOptions", "Quality") => "BrotliCompressionOptions",
            ("System.IO.Compression.ZLibCompressionOptions", "CompressionLevel") => "ZLibCompressionOptions",
            ("System.IO.Compression.ZLibCompressionOptions", "CompressionStrategy") => "ZLibCompressionOptions",
            _ => null,
        };
        if (shortTypeName is null) return;

        if (!AlwaysThrows(shortTypeName, propertyName, rawValue, out var reason)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, assignment.GetLocation(), shortTypeName, propertyName, rawValue, reason));
    }

    private static bool AlwaysThrows(string typeName, string propertyName, int rawValue, out string reason)
    {
        reason = propertyName switch
        {
            "Quality" => "valid values are 0-11",
            "CompressionLevel" => "valid values are -1 to 9",
            _ => "valid values are 0-4",
        };
        try
        {
            switch (typeName, propertyName)
            {
                case ("BrotliCompressionOptions", "Quality"):
                    { var o = new BrotliCompressionOptions(); o.Quality = rawValue; break; }
                case ("ZLibCompressionOptions", "CompressionLevel"):
                    { var o = new ZLibCompressionOptions(); o.CompressionLevel = rawValue; break; }
                case ("ZLibCompressionOptions", "CompressionStrategy"):
                    { var o = new ZLibCompressionOptions(); o.CompressionStrategy = (ZLibCompressionStrategy)rawValue; break; }
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
