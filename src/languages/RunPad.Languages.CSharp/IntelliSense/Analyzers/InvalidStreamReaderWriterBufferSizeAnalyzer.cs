using System.Collections.Immutable;
using System.IO;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `StreamReader`/`StreamWriter` constructors taking a
// `bufferSize` parameter, bundled under one diagnostic (precedent RPAD0426). Confirmed a genuine
// SENTINEL asymmetry between the two constructor families sharing the same parameter name: the
// `Stream`-typed overloads (`StreamReader(Stream, Encoding, bool, int bufferSize[, bool])`/
// `StreamWriter(Stream, Encoding, int bufferSize[, bool])`) treat `bufferSize == -1` as a valid "use
// default" sentinel (confirmed no-throw) — only `0` or values `< -1` throw
// `ArgumentOutOfRangeException`. The `string`-path-typed overloads
// (`StreamReader(string, Encoding, bool, int bufferSize)`/`StreamWriter(string, bool, Encoding, int
// bufferSize)`) do NOT get this exemption — confirmed `-1` throws there too, only `bufferSize > 0` is
// valid. This is exactly the sentinel-value trap already seen with `MemoryPool<T>.Rent`, but this time
// the exemption is conditional on WHICH overload is called, not universal to the parameter name —
// confirmed empirically rather than assumed by analogy between the two constructor families sharing a
// parameter name. Confirmed genuinely distinct from the existing `InvalidPathEmbeddedNulLiteralAnalyzer`
// (RPAD0433-ish), which is scoped to an embedded-NUL check on unrelated path-string arguments (confirmed
// by reading its doc comment, which explicitly notes it deliberately left `StreamReader`/`StreamWriter`'s
// buffer-size shapes for a possible follow-up). The `Stream`-based overloads are probed via the real
// constructor against a throwaway `MemoryStream` (no side effects). The `string`-path-based overloads
// are NOT probed via the real constructor — doing so would require creating/deleting a real temp file
// on every analyzer pass during live editing, a real filesystem side effect this catalog avoids
// throughout. Instead, confirmed empirically (outside the analyzer, including against a NONEXISTENT
// path) that the `bufferSize` check fires unconditionally BEFORE any file-path resolution — so the
// string-path branch uses a plain sign check with no real API call, the same "hand-rolled check, no
// probe" precedent used when probing isn't side-effect-safe (e.g. `NegativeCapacityArgumentAnalyzer`/
// RPAD0231-style cases). No `[SupportedOSPlatform]` gating. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStreamReaderWriterBufferSizeAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0504";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "StreamReader/StreamWriter bufferSize is invalid",
        messageFormat: "'new {0}(...)' always throws ArgumentOutOfRangeException at runtime — {1}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StreamReader/StreamWriter's Stream-based constructors throw for bufferSize == 0 or < -1 (-1 itself is a valid \"default\" sentinel); their string-path-based constructors throw for any bufferSize <= 0, with no -1 exemption.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

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
            } ctor)
        {
            return;
        }
        var typeName = containingType.ToDisplayString();
        var shortName = typeName switch
        {
            "System.IO.StreamReader" => "StreamReader",
            "System.IO.StreamWriter" => "StreamWriter",
            _ => null,
        };
        if (shortName is null) return;

        var bufferSizeIndex = -1;
        for (var i = 0; i < ctor.Parameters.Length; i++)
        {
            if (ctor.Parameters[i].Name != "bufferSize") continue;
            bufferSizeIndex = i;
            break;
        }
        if (bufferSizeIndex < 0) return;
        if (creation.ArgumentList is not { } argumentList || argumentList.Arguments.Count <= bufferSizeIndex)
            return;
        if (ctor.Parameters is not [{ } firstParam, ..]) return;
        var isStreamBased = firstParam.Type.SpecialType != SpecialType.System_String;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(argumentList.Arguments[bufferSizeIndex].Expression, ct).Value is not int bufferSize)
            return;

        if (isStreamBased)
        {
            if (bufferSize == -1) return;
            if (bufferSize > 0) return;

            using var ms = new MemoryStream();
            try
            {
                if (shortName == "StreamReader") _ = new StreamReader(ms, Encoding.UTF8, true, bufferSize);
                else _ = new StreamWriter(ms, Encoding.UTF8, bufferSize);
            }
            catch (System.ArgumentOutOfRangeException)
            {
                context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), shortName, "bufferSize must be greater than zero, or exactly -1 for the default"));
            }
        }
        else
        {
            // No real-constructor probe here — confirmed empirically (outside this analyzer, including
            // against a nonexistent path) that bufferSize validation fires before any file-path
            // resolution, so a plain sign check is safe and avoids real filesystem I/O on every pass.
            if (bufferSize > 0) return;
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), shortName, "bufferSize must be greater than zero"));
        }
    }
}
