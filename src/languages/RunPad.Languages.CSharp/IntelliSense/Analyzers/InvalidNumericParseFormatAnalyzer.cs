using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// TODO.md #74 (триста шестьдесят третий проход, batch) — own pick, not a Roslynator port. Sibling of
// IntegralParseOverflowAnalyzer (RPAD0258) — that analyzer covers a NUMERIC-but-too-large literal
// (`byte.Parse("300")`, OverflowException); this one covers the opposite failure mode, a literal that
// isn't numeric AT ALL (`int.Parse("abc")`, FormatException) — RPAD0258's own `LooksLikePlainInteger
// Literal` pre-filter requires every character to be an ASCII digit, so a letter-containing literal
// like `"abc"` was never in its scope, confirmed by reading its source rather than assumed, so there is
// no overlap between the two. Scoped to a literal containing at least one Unicode LETTER character
// other than `'e'`/`'E'` (the valid scientific-notation marker for floating-point types) — a Unicode
// letter (category Lu/Ll/Lo/etc.) is categorically never a valid digit character (category Nd) in ANY
// culture's `NumberFormatInfo`, so this precondition alone proves invalidity without needing to probe
// multiple cultures at analysis time; still confirmed empirically across five culturally-diverse
// `CurrentCulture` settings (en-US/ar-SA/th-TH/hi-IN/fa-IR, the last three specifically for their
// non-Latin native digit scripts) that letter-containing literals fail identically everywhere, as an
// extra layer of confidence beyond the structural argument alone. Covers `byte`/`sbyte`/`short`/
// `ushort`/`int`/`uint`/`long`/`ulong`/`float`/`double`/`decimal`/`Half`/`Int128`/`UInt128`.Parse(string)
// — a superset of RPAD0258's type list, since `float`/`double` (which RPAD0258 deliberately excludes,
// because they saturate to ±Infinity instead of overflowing) still throw FormatException for a
// non-numeric literal exactly like the integral types do.
//
// EXTENDED проход 438 (TODO #74, candidate #4 from the sixth research subagent survey, Tier 3 — "gap in
// an existing analyzer, extend rather than new file") to also match the `System.Convert.ToXxx(string)`
// family (`ToByte`/`ToSByte`/`ToInt16`/`ToUInt16`/`ToInt32`/`ToUInt32`/`ToInt64`/`ToUInt64`/`ToSingle`/
// `ToDouble`/`ToDecimal`) — confirmed empirically, matching the agent's report, that all eleven throw the
// identical `FormatException` for the identical non-numeric-literal condition as the corresponding
// `Xxx.Parse(string)`. `Convert` has no `Half`/`Int128`/`UInt128` overloads, so those three stay Parse-
// only. Since `Convert.ToXxx`'s `ContainingType` is always `System.Convert` (not the target numeric type,
// unlike `Parse`), the target-type keyword is now derived by METHOD NAME for `Convert` calls instead of
// by `ContainingType.SpecialType`.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidNumericParseFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0356";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Non-numeric literal always fails to parse",
        messageFormat: "'{0}' always throws FormatException at runtime — '{1}' is not a valid {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "byte/sbyte/short/ushort/int/uint/long/ulong/float/double/decimal/Half/Int128/UInt128.Parse(string), and the corresponding System.Convert.ToXxx(string) methods, throw FormatException when the literal contains a letter (other than the 'e'/'E' exponent marker), regardless of culture.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.InvocationExpression);
    }

    private static readonly ImmutableHashSet<string> ConvertMethodNames = ImmutableHashSet.Create(
        "ToByte", "ToSByte", "ToInt16", "ToUInt16", "ToInt32", "ToUInt32",
        "ToInt64", "ToUInt64", "ToSingle", "ToDouble", "ToDecimal");

    private static void Analyze(SyntaxNodeAnalysisContext context)
    {
        var invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: var methodName })
            return;
        var isParse = methodName == "Parse";
        if (!isParse && !ConvertMethodNames.Contains(methodName)) return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;
        if (!ContainsDisqualifyingLetter(value)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol
            {
                Parameters.Length: 1,
            } method || method.Name != methodName)
        {
            return;
        }

        var typeKeyword = isParse ? GetTargetTypeKeyword(method) : GetConvertTargetTypeKeyword(method);
        if (typeKeyword is null) return;
        if (!AlwaysThrows(typeKeyword, value)) return;

        var callText = isParse ? $"{typeKeyword}.Parse(...)" : $"Convert.{methodName}(...)";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), callText, value, typeKeyword));
    }

    private static bool ContainsDisqualifyingLetter(string value)
    {
        foreach (var c in value)
        {
            if (char.IsLetter(c) && c is not ('e' or 'E')) return true;
        }
        return false;
    }

    private static string? GetTargetTypeKeyword(IMethodSymbol method)
    {
        var special = method.ContainingType?.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Single => "float",
            SpecialType.System_Double => "double",
            SpecialType.System_Decimal => "decimal",
            _ => null,
        };
        if (special is not null) return special;

        return method.ContainingType?.ToDisplayString() switch
        {
            "System.Half" => "Half",
            "System.Int128" => "Int128",
            "System.UInt128" => "UInt128",
            _ => null,
        };
    }

    private static string? GetConvertTargetTypeKeyword(IMethodSymbol method)
    {
        if (method.ContainingType?.ToDisplayString() != "System.Convert") return null;
        return method.Name switch
        {
            "ToByte" => "byte",
            "ToSByte" => "sbyte",
            "ToInt16" => "short",
            "ToUInt16" => "ushort",
            "ToInt32" => "int",
            "ToUInt32" => "uint",
            "ToInt64" => "long",
            "ToUInt64" => "ulong",
            "ToSingle" => "float",
            "ToDouble" => "double",
            "ToDecimal" => "decimal",
            _ => null,
        };
    }

    private static bool AlwaysThrows(string typeKeyword, string value)
    {
        var culture = CultureInfo.InvariantCulture;
        return typeKeyword switch
        {
            "byte" => !byte.TryParse(value, NumberStyles.Number, culture, out _),
            "sbyte" => !sbyte.TryParse(value, NumberStyles.Number, culture, out _),
            "short" => !short.TryParse(value, NumberStyles.Number, culture, out _),
            "ushort" => !ushort.TryParse(value, NumberStyles.Number, culture, out _),
            "int" => !int.TryParse(value, NumberStyles.Number, culture, out _),
            "uint" => !uint.TryParse(value, NumberStyles.Number, culture, out _),
            "long" => !long.TryParse(value, NumberStyles.Number, culture, out _),
            "ulong" => !ulong.TryParse(value, NumberStyles.Number, culture, out _),
            "float" => !float.TryParse(value, NumberStyles.Float, culture, out _),
            "double" => !double.TryParse(value, NumberStyles.Float, culture, out _),
            "decimal" => !decimal.TryParse(value, NumberStyles.Number, culture, out _),
            "Half" => !Half.TryParse(value, NumberStyles.Float, culture, out _),
            "Int128" => !Int128.TryParse(value, NumberStyles.Number, culture, out _),
            "UInt128" => !UInt128.TryParse(value, NumberStyles.Number, culture, out _),
            _ => false,
        };
    }
}
