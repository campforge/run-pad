using Invoklet.Contracts;
using Invoklet.Format.MdRun;

namespace Invoklet.Services;

/// <summary>A language a new .inv file can be created in, as the menus offer it.</summary>
public sealed record NewFileLanguageChoice(MdRunLanguage Language, string DisplayName, string? IconResourceKey)
{
    // The registered languages, in registration order. One the .inv format has no language value for
    // can't be written to a file, so it isn't offered.
    public static IReadOnlyList<NewFileLanguageChoice> From(ILanguageHostRegistry registry)
    {
        var languages = new List<NewFileLanguageChoice>();
        foreach (var descriptor in registry.Languages)
            if (MdRunLanguageExtensions.TryFromLanguageId(descriptor.Id, out var language))
                languages.Add(new NewFileLanguageChoice(language, descriptor.DisplayName, descriptor.IconResourceKey));
        return languages;
    }
}
