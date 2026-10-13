using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. Same
// "literal argument that always throws" theme as the other Parse-family analyzers, but narrower and
// deliberately NOT the "run the real validator" technique used elsewhere for format validity:
// `byte.Parse(s)`/`sbyte.Parse(s)`/`short.Parse(s)`/.../`Convert.ToByte(s)`/etc. throw
// `OverflowException` when `s` is a plain (optionally signed) run of ASCII digits whose numeric value
// exceeds the target integral type's range — e.g. `byte.Parse("300")`. Scoped to literals that already
// LOOK like a plain integer (no letters, no decimal point, no group separators) specifically to stay out
// of format-validity territory, which CultureSensitiveNumericParseAnalyzer (RPAD0098) already documents
// as deliberately NOT extended to the integral types, because NumberStyles.Integer's default parsing of
// a plain digit run has no decimal/group separator to be culture-sensitive about — confirmed empirically
// across five culturally-diverse CurrentCulture settings (en-US/de-DE/ar-SA/fr-FR/hi-IN) before writing
// this, all in exact agreement on every case. The actual bounds check still calls the target type's own
// `TryParse` (with NumberStyles.Integer and CultureInfo.InvariantCulture, matching the no-arg overload's
// own default style) rather than hand-copying each type's Min/MaxValue, so it can't drift from the real
// implementation's actual bounds.
// Extended to also cover `decimal.Parse(s)`/
// `Convert.ToDecimal(s)` — decimal.Parse also throws OverflowException for a plain-digit literal outside
// decimal's range (unlike double/float.Parse, which have saturated to ±Infinity instead of throwing
// since .NET Core 3.0, confirmed empirically before extending — so this was deliberately NOT extended to
// double/float). A plain-digit-only literal (still the same pre-filter as the integral case, no decimal
// point or group separator present) is culture-invariant for decimal.Parse too, re-confirmed empirically
// across the same five cultures before extending.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class IntegralParseOverflowAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0258";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "Integer-shaped literal is out of range for the target numeric type",
        messageFormat: "'{0}(...)' always throws OverflowException at runtime — '{1}' is out of range for {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "byte/sbyte/short/ushort/int/uint/long/ulong.Parse (and the matching Convert.ToXxx overloads) throw OverflowException when the input's numeric value doesn't fit the target type, regardless of culture.");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Parse" or "ToByte" or "ToSByte" or "ToInt16" or "ToUInt16" or "ToInt32" or "ToUInt32" or "ToInt64" or "ToUInt64" or "ToDecimal" })
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var arg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(arg, ct).Value is not string value) return;
        if (!LooksLikePlainIntegerLiteral(value)) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol method) return;

        var typeKeyword = GetTargetTypeKeyword(method);
        if (typeKeyword is null) return;
        if (TryParseAsTargetType(typeKeyword, value)) return;

        var displayName = method.Name == "Parse" ? $"{typeKeyword}.Parse" : $"Convert.{method.Name}";
        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), displayName, value, typeKeyword));
    }

    private static bool LooksLikePlainIntegerLiteral(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return false;
        var start = trimmed[0] is '+' or '-' ? 1 : 0;
        if (start == trimmed.Length) return false;
        for (var i = start; i < trimmed.Length; i++)
        {
            if (trimmed[i] < '0' || trimmed[i] > '9') return false;
        }
        return true;
    }

    private static string? GetTargetTypeKeyword(IMethodSymbol method)
    {
        if (method.Name == "Parse")
        {
            return method.ContainingType?.SpecialType switch
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
                _ => null,
            };
        }

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
            "ToDecimal" => "decimal",
            _ => null,
        };
    }

    private static bool TryParseAsTargetType(string typeKeyword, string value) => typeKeyword switch
    {
        "byte" => byte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "sbyte" => sbyte.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "short" => short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "ushort" => ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "int" => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "uint" => uint.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "long" => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "ulong" => ulong.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        "decimal" => decimal.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        _ => true,
    };
}
