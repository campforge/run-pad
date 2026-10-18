using RunPad.Contracts;
using RunPad.Format.RPad;

namespace RunPad.Services;

/// <summary>A language a new .rpad file can be created in, as the menus offer it.</summary>
public sealed record NewFileLanguageChoice(RpadLanguage Language, string DisplayName, string? IconResourceKey)
{
    // The registered languages, in registration order. One the .rpad format has no language value for
    // can't be written to a file, so it isn't offered.
    public static IReadOnlyList<NewFileLanguageChoice> From(ILanguageHostRegistry registry)
    {
        var languages = new List<NewFileLanguageChoice>();
        foreach (var descriptor in registry.Languages)
            if (RpadLanguageExtensions.TryFromLanguageId(descriptor.Id, out var language))
                languages.Add(new NewFileLanguageChoice(language, descriptor.DisplayName, descriptor.IconResourceKey));
        return languages;
    }
}
