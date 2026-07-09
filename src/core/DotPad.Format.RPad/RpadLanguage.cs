namespace DotPad.Format.RPad;

// Supported values in format version 1 — see docs/SpecificationRPad.md § language. The enum member's
// own lowercased name is the id used everywhere (YAML `language:` value, code-fence language, and
// ILanguageHost/ILanguageHostFactory.Language) — keep new members' casing consistent with that.
public enum RpadLanguage
{
    Unknown = 0,
    CSharp = 1,
    Vb = 2,
}
