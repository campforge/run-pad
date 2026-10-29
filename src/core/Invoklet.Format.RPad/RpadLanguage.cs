namespace Invoklet.Format.RPad;

// Supported values in format version 1. The enum member's
// own lowercased name is the id used everywhere (YAML `language:` value, code-fence language, and
// ILanguageHost/ILanguageHostFactory.Language) — keep new members' casing consistent with that.
public enum RpadLanguage
{
    Unknown = 0,
    CSharp = 1,
    Vb = 2,
}

public static class RpadLanguageExtensions
{
    // Three call sites (EditorTabItemControl.ResolveLanguageId, Invoklet.Cli.CliRunner,
    // and FileTreeViewModel's language-icon lookup) each hand-rolled this same
    // `.ToString().ToLowerInvariant()` conversion independently — centralized here since it's really a
    // fact about RpadLanguage's own naming convention (see the enum's doc comment above), not
    // something each call site should re-derive.
    public static string ToLanguageId(this RpadLanguage language) => language.ToString().ToLowerInvariant();

    // The member a language id names ("csharp", "vb"), ignoring case; false for Unknown or no member.
    public static bool TryFromLanguageId(string id, out RpadLanguage language)
    {
        language = Enum.GetValues<RpadLanguage>()
            .FirstOrDefault(l => l != RpadLanguage.Unknown && string.Equals(l.ToLanguageId(), id, StringComparison.OrdinalIgnoreCase));
        return language != RpadLanguage.Unknown;
    }
}
