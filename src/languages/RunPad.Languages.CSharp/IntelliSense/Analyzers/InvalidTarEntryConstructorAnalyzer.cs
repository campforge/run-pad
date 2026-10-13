using System.Collections.Immutable;
using System.Formats.Tar;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Formats.Tar` — first analyzer in this catalog
// touching this namespace. `PaxTarEntry`/`GnuTarEntry`/`UstarTarEntry`/`V7TarEntry(TarEntryType, string)`
// constructors — each of the 4 concrete entry types only accepts a DIFFERENT subset of the 18-member
// `TarEntryType` enum, a richer shape than a flat invalid-enum-range check (same class of "valid enum
// member, wrong for THIS specific receiver" already established by `InvalidJsonOptionsPropertyAnalyzer`'s
// `JsonDocumentOptions.CommentHandling`/RPAD0453). Confirmed empirically: `Pax`/`Gnu`/`Ustar` all accept
// exactly the same 7 (`RegularFile`, `HardLink`, `SymbolicLink`, `CharacterDevice`, `BlockDevice`,
// `Directory`, `Fifo`) and reject the other 11; `V7TarEntry` has a DIFFERENT accepted subset
// (`V7RegularFile`, `HardLink`, `SymbolicLink`, `Directory`) and notably REJECTS plain `RegularFile`,
// which every other entry type accepts — confirmed rather than assumed, since a naive guess would expect
// `RegularFile` to be universally valid. `TarEntryType`'s underlying type is `byte`, not `int` (confirmed
// via `Enum.GetUnderlyingType`, per this session's own enum-underlying-type lesson — an out-of-range cast
// like `(TarEntryType)999` doesn't even compile against a byte-backed enum, so this analyzer only needs
// to run the real validator against the enum's OWN defined members, never a raw out-of-range cast).
// Confirmed no `[SupportedOSPlatform]`/`[UnsupportedOSPlatform]` gating on any of the 4 types. Confirmed
// no side effects — construction is pure in-memory metadata, no filesystem/stream I/O. Confirmed invariant
// across en-US/de-DE/ar-SA/ja-JP. Throws `ArgumentException`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidTarEntryConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0466";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "TarEntry constructor argument is not a supported entry type for this format",
        messageFormat: "'new {0}(...)' always throws ArgumentException at runtime — {1} is not a supported TarEntryType for {0}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PaxTarEntry/GnuTarEntry/UstarTarEntry/V7TarEntry(TarEntryType, string) throw ArgumentException when the TarEntryType isn't in that specific entry type's own supported subset.");

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
        if (creation.ArgumentList is not { Arguments: [{ Expression: var entryTypeArg }, { Expression: var nameArg }, ..] })
            return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(entryTypeArg, ct).Value is not byte rawValue) return;
        if (context.SemanticModel.GetConstantValue(nameArg, ct).Value is not string name) return;

        if (context.SemanticModel.GetSymbolInfo(creation, ct).Symbol is not IMethodSymbol { MethodKind: MethodKind.Constructor, ContainingType: { } containingType })
            return;
        var typeName = containingType.ToDisplayString();
        var shortName = typeName switch
        {
            "System.Formats.Tar.PaxTarEntry" => "PaxTarEntry",
            "System.Formats.Tar.GnuTarEntry" => "GnuTarEntry",
            "System.Formats.Tar.UstarTarEntry" => "UstarTarEntry",
            "System.Formats.Tar.V7TarEntry" => "V7TarEntry",
            _ => null,
        };
        if (shortName is null) return;

        var entryType = (TarEntryType)rawValue;
        try
        {
            switch (shortName)
            {
                case "PaxTarEntry": _ = new PaxTarEntry(entryType, name); break;
                case "GnuTarEntry": _ = new GnuTarEntry(entryType, name); break;
                case "UstarTarEntry": _ = new UstarTarEntry(entryType, name); break;
                default: _ = new V7TarEntry(entryType, name); break;
            }
        }
        catch (System.ArgumentException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), shortName, entryType));
        }
    }
}
