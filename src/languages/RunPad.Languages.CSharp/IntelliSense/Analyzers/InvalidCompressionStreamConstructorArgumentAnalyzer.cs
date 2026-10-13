using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "literal
// argument that always throws" theme as NegativeCapacityArgumentAnalyzer (RPAD0231): `new
// GZipStream(Stream, CompressionLevel)`/`DeflateStream(...)`/`ZLibStream(...)`/`BrotliStream(...)`
// throw when the `CompressionLevel` argument (typically an explicit numeric cast, e.g.
// `(CompressionLevel)99`) falls outside the enum's valid `[0, 3]` range — confirmed empirically via
// scratch `dotnet run` script across all four stream types, including a genuinely surprising detail:
// `GZipStream`/`DeflateStream`/`ZLibStream` throw `ArgumentOutOfRangeException`, but `BrotliStream`
// throws a DIFFERENT exception type, `ArgumentException`, for the identical out-of-range value —
// confirmed rather than assumed, handled via a per-type exception-type lookup.
// Self-correction caught via a throwaway Roslyn symbol-resolution check (not PROBE — this one never
// got as far as PROBE): `GetConstantValue` does NOT look through an explicit cast to an enum type —
// calling it directly on the `(CompressionLevel)99` cast expression returns `HasValue: false`, even
// though the literal `99` inside it is trivially constant. Fixed by unwrapping a
// `CastExpressionSyntax` argument to its inner `Expression` before calling `GetConstantValue`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidCompressionStreamConstructorArgumentAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0354";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Compression stream CompressionLevel argument is out of range",
        messageFormat: "'new {0}(...)' always throws {1} at runtime — {2} is not a valid CompressionLevel",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "new GZipStream/DeflateStream/ZLibStream/BrotliStream(stream, compressionLevel) throws when compressionLevel is outside the enum's valid [0, 3] range (ArgumentOutOfRangeException for GZipStream/DeflateStream/ZLibStream, ArgumentException for BrotliStream).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    private static readonly ImmutableDictionary<string, string> ExceptionTypeByTypeName = ImmutableDictionary.CreateRange(
        [
            KeyValuePair.Create("System.IO.Compression.GZipStream", "ArgumentOutOfRangeException"),
            KeyValuePair.Create("System.IO.Compression.DeflateStream", "ArgumentOutOfRangeException"),
            KeyValuePair.Create("System.IO.Compression.ZLibStream", "ArgumentOutOfRangeException"),
            KeyValuePair.Create("System.IO.Compression.BrotliStream", "ArgumentException"),
        ]);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.ObjectCreationExpression, SyntaxKind.ImplicitObjectCreationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var creation = (BaseObjectCreationExpressionSyntax)context.Node;
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var levelArg }] }) return;
        if (levelArg is CastExpressionSyntax cast) levelArg = cast.Expression;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(levelArg, ct).Value is not int level || (level is >= 0 and <= 3))
            return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor } ctor)
            return;
        var typeName = ctor.ContainingType?.ToDisplayString();
        if (typeName is null || !ExceptionTypeByTypeName.TryGetValue(typeName, out var exceptionType)) return;
        if (ctor.Parameters is not [_, { Type: { TypeKind: TypeKind.Enum } enumType }]
            || enumType.ToDisplayString() != "System.IO.Compression.CompressionLevel")
        {
            return;
        }

        var shortTypeName = typeName[(typeName.LastIndexOf('.') + 1)..];
        context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), shortTypeName, exceptionType, level));
    }
}
