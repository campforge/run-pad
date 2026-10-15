using RunPad.Format.RPad;

namespace RunPad.Contracts;

/// <summary>Code templates with a <c>${0}</c> caret marker, as snippets and
/// <see cref="ILanguageHostFactory.CodeForKind"/> return them.</summary>
public static class CodeTemplate
{
    public const string CaretMarker = "${0}";

    /// <summary>The text without the marker, and where the marker was (-1 without one).</summary>
    public static (string Text, int Caret) TakeCaret(string code)
    {
        var at = code.IndexOf(CaretMarker, StringComparison.Ordinal);
        return at < 0 ? (code, -1) : (code.Remove(at, CaretMarker.Length), at);
    }

    /// <summary>The code a new script of <paramref name="language"/> and <paramref name="kind"/> starts
    /// with, caret marker included: the kind's template (a Program's <c>Main</c>), or empty.</summary>
    public static string NewScriptCode(this ILanguageHostRegistry registry, RpadLanguage language, RpadKind kind)
        => registry.GetCodeForKind(language.ToLanguageId(), "", kind) ?? "";
}
