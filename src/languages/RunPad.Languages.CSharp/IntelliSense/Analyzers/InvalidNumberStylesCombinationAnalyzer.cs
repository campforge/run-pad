using System.Collections.Immutable;
using System.Globalization;
using System.Numerics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same "run
// the real validator" theme as InvalidRegexOptionsCombinationAnalyzer (RPAD0274), applied to
// a different flags enum: `Xxx.Parse(s, NumberStyles style[, IFormatProvider])` throws ArgumentException
// when `style` combines `NumberStyles.AllowHexSpecifier` with most other flags (confirmed empirically
// before writing this — e.g. `AllowHexSpecifier | AllowDecimalPoint` throws for every numeric type
// tried). Crucially, the exact compatible subset differs BY TARGET TYPE — `int.Parse`/`long.Parse` allow
// `AllowHexSpecifier` alone or with only the whitespace flags, but `decimal.Parse` rejects
// `AllowHexSpecifier` in EVERY combination, even alone — confirmed empirically rather than assumed, so
// this analyzer dispatches to the REAL `TryParse` of the SPECIFIC resolved target type (not a stand-in
// like `int`) on a literal `NumberStyles` value with a universally-parseable placeholder string ("1",
// valid under any NumberStyles combination — no thousands/decimal separators, no sign, a valid hex digit
// too), reporting only if that call itself throws — the placeholder string means the user's own string
// argument doesn't need to be a literal at all. `TryParse` (not `Parse`) is used to avoid the analyzer's
// own bare-throwing-catch shape triggering EmptyCatchClauseAnalyzer/RPAD0001 on itself.
// Extended to also cover `System.Numerics.BigInteger.Parse` —
// confirmed empirically (scratch `dotnet run` script) to follow the same int/long-shaped compatible
// subset (AllowHexSpecifier alone or with only the whitespace flags). `BigInteger` isn't a Roslyn
// `SpecialType`, so it's matched by name/namespace instead of the `SpecialType` switch used for the
// built-in numeric types.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidNumberStylesCombinationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0275";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "NumberStyles combination always throws",
        messageFormat: "'{0}.Parse(...)' always throws ArgumentException at runtime — this NumberStyles combination is not valid together",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "NumberStyles.AllowHexSpecifier is only compatible with a small subset of the other flags (varying by numeric type); combining it outside that subset throws ArgumentException.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" })
            return;
        if (invocation.ArgumentList.Arguments is not [_, { Expression: var stylesArg }, ..]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(stylesArg, ct).Value is not int stylesValue) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "Parse" } method)
            return;
        if (method.Parameters is not [_, { Type.Name: "NumberStyles" }, ..]) return;

        var typeKeyword = method.ContainingType?.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Decimal => "decimal",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            _ => null,
        };
        if (typeKeyword is null)
        {
            if (method.ContainingType is not { Name: "BigInteger" } biType
                || biType.ContainingNamespace?.ToDisplayString() != "System.Numerics")
            {
                return;
            }
            typeKeyword = "BigInteger";
        }

        if (!AlwaysThrows(typeKeyword, (NumberStyles)stylesValue)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), typeKeyword));
    }

    private static bool AlwaysThrows(string typeKeyword, NumberStyles styles)
    {
        const string placeholder = "1";
        var ok = typeKeyword switch
        {
            "byte" => TryParseSafely(() => byte.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "sbyte" => TryParseSafely(() => sbyte.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "short" => TryParseSafely(() => short.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "ushort" => TryParseSafely(() => ushort.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "int" => TryParseSafely(() => int.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "uint" => TryParseSafely(() => uint.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "long" => TryParseSafely(() => long.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "ulong" => TryParseSafely(() => ulong.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "decimal" => TryParseSafely(() => decimal.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "float" => TryParseSafely(() => float.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            "double" => TryParseSafely(() => double.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
            _ => TryParseSafely(() => BigInteger.TryParse(placeholder, styles, CultureInfo.InvariantCulture, out _)),
        };
        return ok is null;
    }

    private static bool? TryParseSafely(Func<bool> tryParse)
    {
        try
        {
            return tryParse();
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
