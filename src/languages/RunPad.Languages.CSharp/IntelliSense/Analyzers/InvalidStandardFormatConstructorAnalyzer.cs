using System.Buffers;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// `System.Buffers.StandardFormat(char symbol, byte
// precision = StandardFormat.NoPrecision)` — first touch of `System.Buffers.StandardFormat`.
// Confirmed empirically: throws `ArgumentOutOfRangeException` ("Precision cannot be larger than 99")
// whenever `precision` is in `[100, 254]` — i.e. greater than `MaxPrecision` (99) but not equal to the
// `NoPrecision` sentinel (255); confirmed exact boundary (99 valid, 100 throws, 254 throws, 255 valid).
// `symbol`'s value doesn't affect this check. A plain integer literal for `precision` (e.g. `100`)
// constant-folds as boxed `int`, not `byte` — same normalization already applied elsewhere in this
// catalog for implicit numeric conversions. No `[SupportedOSPlatform]` gating (reflection: only
// `IsReadOnlyAttribute`), no side effects — pure struct construction. Culture-irrelevant (no
// parsing/formatting performed).
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidStandardFormatConstructorAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0492";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "StandardFormat precision is out of range",
        messageFormat: "'new StandardFormat(...)' always throws ArgumentOutOfRangeException at runtime — precision cannot be larger than 99 (255 is the reserved \"no precision\" sentinel)",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "StandardFormat(char, byte precision) throws ArgumentOutOfRangeException when precision is greater than 99 and not equal to 255 (the NoPrecision sentinel).");

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
        if (containingType.ToDisplayString() != "System.Buffers.StandardFormat") return;

        var precisionIndex = -1;
        for (var i = 0; i < ctor.Parameters.Length; i++)
        {
            if (ctor.Parameters[i].Name != "precision") continue;
            precisionIndex = i;
            break;
        }
        if (precisionIndex < 0) return;
        if (creation.ArgumentList is not { } argumentList || argumentList.Arguments.Count <= precisionIndex)
            return;

        var ct = context.CancellationToken;
        if (ToByteConstant(context.SemanticModel.GetConstantValue(argumentList.Arguments[precisionIndex].Expression, ct)) is not { } precision)
            return;

        try
        {
            _ = new StandardFormat('X', precision);
        }
        catch (System.ArgumentOutOfRangeException)
        {
            context.ReportDiagnostic(Diagnostic.Create(Rule, creation.GetLocation()));
        }
    }

    private static byte? ToByteConstant(Optional<object?> constant) => constant.Value switch
    {
        byte b => b,
        int i and >= 0 and <= 255 => (byte)i,
        _ => null,
    };
}
