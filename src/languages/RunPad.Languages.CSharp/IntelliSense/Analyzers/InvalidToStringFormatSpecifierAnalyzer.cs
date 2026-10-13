using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace RunPad.Languages.CSharp.IntelliSense.Analyzers;

// Own pick, not a Roslynator port. A correctness
// finding: `value.ToString(format)` throws FormatException for a `format` string that isn't a
// recognized standard or custom format specifier for the receiver's type — e.g. `42.ToString("Z")`,
// `TimeSpan.Zero.ToString("Z")`, `DateTime.MinValue.ToString("Z")` — confirmed empirically via scratch
// `dotnet run` script across five culturally-diverse `CurrentCulture` settings (en-US/ar-SA/th-TH/
// de-DE/ja-JP) that format-STRING validity itself (as opposed to the resulting formatted string's
// content) is culture-invariant. Uses the "run the real validator" technique rather than hand-deriving
// each type's valid specifier set (which genuinely differs per type — confirmed empirically that "X"
// is valid for `int` but NOT for `double`): probes a FIXED representative value of the receiver's own
// static type (`0`/`0L`/`0.0`/`TimeSpan.Zero`/`DateTime.MinValue`, etc.) with the literal format string,
// since format-string validity is a property of the (format string, type) pair, not of the specific
// value being formatted — so the receiver expression itself doesn't need to be a literal, only its
// static type needs to be known and its format argument needs to be a literal string. Scoped to the
// built-in numeric types, `TimeSpan`, and `DateTime`.
// Extended to also cover `Guid`/`DateOnly`/`TimeOnly` —
// explicitly deferred earlier, now confirmed empirically (again across culturally-diverse
// `CurrentCulture` settings) to follow the identical "run the real validator" pattern with their own
// representative values (`Guid.Empty`/`DateOnly.MinValue`/`TimeOnly.MinValue`).
// Extended again (re-verified
// independently before implementing) to also cover `DateTimeOffset`/`BigInteger`/`Int128`/`UInt128`/
// `Half` — the agent found these missing from the type switch below via a systematic sweep, confirmed
// empirically to follow the identical pattern (own representative value: `DateTimeOffset.MinValue`/
// `BigInteger.Zero`/`(Int128)0`/`(UInt128)0`/`(Half)0`), including `DateTimeOffset`'s culture-invariance
// re-confirmed independently across the same four cultures already used for this file's other types.
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidToStringFormatSpecifierAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "RPAD0346";

    private static readonly DiagnosticDescriptor Rule = new(
        DiagnosticId,
        title: "ToString format specifier is invalid for this type",
        messageFormat: "'{0}' always throws FormatException at runtime — '{1}' is not a recognized format specifier for {2}",
        category: "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "value.ToString(format) throws FormatException when format isn't a recognized standard or custom format specifier for the value's type (numeric types, TimeSpan, DateTime, Guid, DateOnly, TimeOnly).");

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
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "ToString" } access)
            return;
        if (invocation.ArgumentList.Arguments is not [{ Expression: var formatArg }]) return;

        var ct = context.CancellationToken;
        if (context.SemanticModel.GetConstantValue(formatArg, ct).Value is not string format) return;

        if (context.SemanticModel.GetSymbolInfo(invocation, ct).Symbol is not IMethodSymbol { Name: "ToString" } method)
            return;
        if (method.Parameters is not [{ Type.SpecialType: SpecialType.System_String }]) return;
        if (method.ContainingType is not { } containingType) return;

        if (!AlwaysThrows(containingType, format, out var typeKeyword)) return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, invocation.GetLocation(), invocation.ToString(), format, typeKeyword));
    }

    private static bool AlwaysThrows(ITypeSymbol containingType, string format, out string typeKeyword)
    {
        // Built-in numeric types have C# keyword aliases (ToDisplayString() would return "int", not
        // "System.Int32"), so SpecialType is used for them; TimeSpan/DateTime have no SpecialType and
        // are matched by fully-qualified name instead.
        typeKeyword = containingType.SpecialType switch
        {
            SpecialType.System_Byte => "byte",
            SpecialType.System_SByte => "sbyte",
            SpecialType.System_Int16 => "short",
            SpecialType.System_UInt16 => "ushort",
            SpecialType.System_Int32 => "int",
            SpecialType.System_UInt32 => "uint",
            SpecialType.System_Int64 => "long",
            SpecialType.System_UInt64 => "ulong",
            SpecialType.System_Double => "double",
            SpecialType.System_Single => "float",
            SpecialType.System_Decimal => "decimal",
            _ => containingType.ToDisplayString() switch
            {
                "System.TimeSpan" => "TimeSpan",
                "System.DateTime" => "DateTime",
                "System.Guid" => "Guid",
                "System.DateOnly" => "DateOnly",
                "System.TimeOnly" => "TimeOnly",
                "System.DateTimeOffset" => "DateTimeOffset",
                "System.Numerics.BigInteger" => "BigInteger",
                "System.Int128" => "Int128",
                "System.UInt128" => "UInt128",
                "System.Half" => "Half",
                _ => "",
            },
        };
        if (typeKeyword.Length == 0) return false;

        try
        {
            _ = typeKeyword switch
            {
                "byte" => ((byte)0).ToString(format),
                "sbyte" => ((sbyte)0).ToString(format),
                "short" => ((short)0).ToString(format),
                "ushort" => ((ushort)0).ToString(format),
                "int" => 0.ToString(format),
                "uint" => 0u.ToString(format),
                "long" => 0L.ToString(format),
                "ulong" => 0ul.ToString(format),
                "double" => 0.0.ToString(format),
                "float" => 0.0f.ToString(format),
                "decimal" => 0m.ToString(format),
                "TimeSpan" => TimeSpan.Zero.ToString(format),
                "DateTime" => DateTime.MinValue.ToString(format),
                "Guid" => Guid.Empty.ToString(format),
                "DateOnly" => DateOnly.MinValue.ToString(format),
                "TimeOnly" => TimeOnly.MinValue.ToString(format),
                "DateTimeOffset" => DateTimeOffset.MinValue.ToString(format),
                "BigInteger" => System.Numerics.BigInteger.Zero.ToString(format),
                "Int128" => ((Int128)0).ToString(format),
                "UInt128" => ((UInt128)0).ToString(format),
                "Half" => ((Half)0).ToString(format),
                _ => "",
            };
            return false;
        }
        catch (FormatException)
        {
            return true;
        }
    }
}
