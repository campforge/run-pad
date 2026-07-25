namespace RunPad.Format.RPad;

// Supported values in format version 1 — see docs/SpecificationRPad.md § language. The enum member's
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
    // TODO.md #81: three call sites (EditorTabItemControl.ResolveLanguageId, RunPad.Cli.CliRunner,
    // and FileTreeViewModel's language-icon lookup) each hand-rolled this same
    // `.ToString().ToLowerInvariant()` conversion independently — centralized here since it's really a
    // fact about RpadLanguage's own naming convention (see the enum's doc comment above), not
    // something each call site should re-derive.
    public static string ToLanguageId(this RpadLanguage language) => language.ToString().ToLowerInvariant();
}
