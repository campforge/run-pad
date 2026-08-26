using System.Collections.Immutable;
using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (467-й проход, batch) — candidate #4 from the thirtieth research subagent survey,
// re-verified independently before implementing. `System.IO.BufferedStream(Stream stream, int
// bufferSize)` — first touch of `BufferedStream`. Confirmed empirically: `bufferSize <= 0` always
// throws `ArgumentOutOfRangeException` ("'bufferSize' must be greater than zero.") — confirmed BOTH `0`
// and `-1` throw (no `-1` sentinel exemption here, unlike the sibling `StreamReader`/`StreamWriter`
// `Stream`-based constructors covered by RPAD0504 in this same проход — confirmed empirically rather
// than assumed by analogy). No `[SupportedOSPlatform]` gating, no side effects — probed via a
// throwaway `MemoryStream`, never touching real files/OS handles. Culture-irrelevant.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidBufferedStreamConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0505";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "BufferedStream bufferSize is not positive",
        messageFormat: "'new BufferedStream(..., {0})' always throws ArgumentOutOfRangeException at runtime — bufferSize must be greater than zero",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "BufferedStream(Stream, int bufferSize) throws ArgumentOutOfRangeException for a non-positive bufferSize.");

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
                Parameters: [_, { Type.SpecialType: SpecialType.System_Int32 }],
            })
        {
            return;
        }
        if (containingType.ToDisplayString() != "System.IO.BufferedStream") return;
        if (creation.ArgumentList is not { Arguments: [_, { Expression: var bufferSizeArg }] }) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(bufferSizeArg, ct).Value is not int bufferSize) return;
        if (bufferSize > 0) return;

        using var ms = new MemoryStream();
        try
        {
            _ = new BufferedStream(ms, bufferSize);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation(), bufferSize));
        }
    }
}
