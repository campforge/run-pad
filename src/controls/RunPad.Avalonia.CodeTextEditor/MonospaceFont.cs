using Avalonia.Media;

namespace RunPad.Avalonia.CodeTextEditor;

/// <summary>
/// The fixed-pitch font code is shown in when no specific font was chosen. The generic
/// <c>monospace</c> name alone is not enough: Avalonia resolves it to a real monospace face on Linux
/// but to Helvetica, a proportional face, on macOS. The default is the first font of a per-platform
/// list that is installed and actually fixed-pitch — fonts that come with the system; nothing is
/// downloaded or bundled.
/// </summary>
public static class MonospaceFont
{
    /// <summary>The name a code-font setting holds for "the platform's default code font".</summary>
    public const string GenericName = "monospace";

    internal enum Platform { MacOS, Windows, Linux }

    private static readonly Lazy<FontFamily> DefaultFamily =
        new(() => new FontFamily(SelectDefaultName(CandidatesFor(CurrentPlatform), IsInstalledFixedPitch)));

    /// <summary>The platform's default code font, resolved once per process.</summary>
    public static FontFamily Default => DefaultFamily.Value;

    /// <summary>
    /// The font for a code-font setting. An installed family the user named is used as it is, even a
    /// proportional one; the generic name, an empty value or a family that isn't installed here gets
    /// <see cref="Default"/> instead of the theme's proportional font. The setting itself is never
    /// rewritten, so a font installed later is picked up.
    /// </summary>
    public static FontFamily Resolve(string? setting)
        => ResolveName(setting, IsInstalled) is { } name ? new FontFamily(name) : Default;

    // null means "use the default".
    internal static string? ResolveName(string? setting, Func<string, bool> isInstalled)
    {
        var name = setting?.Trim();
        if (string.IsNullOrEmpty(name) || string.Equals(name, GenericName, StringComparison.OrdinalIgnoreCase))
            return null;
        return isInstalled(name) ? name : null;
    }

    internal static Platform CurrentPlatform =>
        OperatingSystem.IsMacOS() ? Platform.MacOS : OperatingSystem.IsWindows() ? Platform.Windows : Platform.Linux;

    // Fonts that come with the system (or its common distributions), most preferred first. Cascadia
    // Mono ships with Windows 11 and Windows Terminal, Consolas since Vista; Menlo is macOS's own code
    // font since 10.6. On Linux the generic fallback below is a fontconfig alias that does resolve to a
    // monospace face, so it is a good last resort there.
    internal static IReadOnlyList<string> CandidatesFor(Platform platform) => platform switch
    {
        Platform.MacOS => ["Menlo", "Monaco", "Courier New"],
        Platform.Windows => ["Cascadia Mono", "Consolas", "Courier New"],
        _ => ["DejaVu Sans Mono", "Liberation Mono", "Noto Sans Mono", "Ubuntu Mono"],
    };

    internal static string SelectDefaultName(IEnumerable<string> candidates, Func<string, bool> isInstalledFixedPitch)
        => candidates.FirstOrDefault(isInstalledFixedPitch) ?? GenericName;

    // A missing family is silently substituted by the font manager (macOS hands back Helvetica for
    // an unknown name), so "installed" means the face it resolves to carries one of the requested names.
    private static bool IsInstalled(string name) => TryGetInstalledFace(name, out _);

    private static bool IsInstalledFixedPitch(string name)
        => TryGetInstalledFace(name, out var face) && IsFixedPitch(face);

    private static bool TryGetInstalledFace(string name, out GlyphTypeface face)
    {
        face = null!;
        try
        {
            if (!FontManager.Current.TryGetGlyphTypeface(new Typeface(new FontFamily(name)), out var resolved))
                return false;
            var requested = name.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (!requested.Any(r => string.Equals(r, resolved.FamilyName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(r, resolved.TypographicFamilyName, StringComparison.OrdinalIgnoreCase)))
                return false;
            face = resolved;
            return true;
        }
        catch (InvalidOperationException)
        {
            // No font manager yet (no Avalonia platform): nothing can be checked.
            return false;
        }
    }

    // The table flag some fonts get wrong is not trusted: narrow, wide and punctuation glyphs must
    // all have the same advance.
    private static bool IsFixedPitch(GlyphTypeface face)
    {
        double? width = null;
        foreach (var c in "iW.m")
        {
            if (!face.CharacterToGlyphMap.TryGetGlyph(c, out var glyph) || glyph == 0
                || !face.TryGetHorizontalGlyphAdvance(glyph, out var advance))
                return false;
            if (width is { } w && w != advance)
                return false;
            width = advance;
        }

        return true;
    }
}
