using Microsoft.CodeAnalysis;

namespace Invoklet.Languages.CSharp.IntelliSense;

internal static class SymbolFormats
{
    // Also used for signature-help method/parameter signatures and quick info, not just completion —
    // IncludeParamsRefOut/IncludeDefaultValue make those read like a real signature (`params object[]
    // args`, `string? label = null`) instead of just bare "type name" pairs.
    internal static readonly SymbolDisplayFormat Brief =
        new(typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
            genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
            parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeName |
                SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeDefaultValue,
            memberOptions: SymbolDisplayMemberOptions.IncludeParameters | SymbolDisplayMemberOptions.IncludeType,
            miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);
}
