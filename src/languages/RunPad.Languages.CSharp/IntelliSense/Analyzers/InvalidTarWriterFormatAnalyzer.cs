using System.Collections.Immutable;
using System.Formats.Tar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (457-й проход, batch) — candidate #8 from the nineteenth research subagent survey,
// re-verified independently before implementing. `new TarWriter(Stream, TarEntryFormat)` — throws
// `ArgumentOutOfRangeException` (param `format`) for `TarEntryFormat.Unknown` (value `0` — notably the
// type's OWN DEFAULT value, confirmed empirically rather than assumed) or any other undefined value;
// `V7`/`Ustar`/`Pax`/`Gnu` (1-4) all construct fine. `TarEntryFormat`'s underlying type is `int`
// (confirmed via `Enum.GetUnderlyingType`). Confirmed no `[SupportedOSPlatform]` gating on `TarWriter`.
// Confirmed no observable side effect from the throw specifically: construction validates `format`
// before writing any bytes to the stream, verified against a throwaway `MemoryStream` per attempt.
// Confirmed invariant across en-US/de-DE/ar-SA/ja-JP.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTarWriterFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0471";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TarWriter format argument is not a defined TarEntryFormat",
        messageFormat: "'new TarWriter(...)' always throws ArgumentOutOfRangeException at runtime — {0} is not a valid TarEntryFormat for writing",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "TarWriter(Stream, TarEntryFormat) throws ArgumentOutOfRangeException for TarEntryFormat.Unknown or any other undefined value.");

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
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var formatArg }, ..] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(formatArg, ct).Value is not int rawValue) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol
            {
                MethodKind: MethodKind.Constructor,
                Parameters: [_, { Type: { } formatType }, ..],
                ContainingType: { } containingType,
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.Formats.Tar.TarWriter") return;
        if (formatType.ToDisplayString() != "System.Formats.Tar.TarEntryFormat") return;

        try
        {
            using var ms = new System.IO.MemoryStream();
            using var w = new TarWriter(ms, (TarEntryFormat)rawValue);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), (TarEntryFormat)rawValue));
        }
    }
}
