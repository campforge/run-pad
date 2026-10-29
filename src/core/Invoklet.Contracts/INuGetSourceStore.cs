namespace Invoklet.Contracts;

/// <summary>One NuGet package source. <see cref="Password"/> is the plain value (decrypted on load).</summary>
public sealed record NuGetSource(
    string Name,
    string Url,
    bool IsEnabled = true,
    string? Username = null,
    string? Password = null)
{
    public bool HasCredentials => !string.IsNullOrEmpty(Username);
}

/// <summary>
/// Invoklet's own NuGet.Config — the package sources used both by package search and by restore of
/// every script build (GUI and CLI). Never the user's global NuGet.Config.
/// </summary>
public interface INuGetSourceStore
{
    /// <summary>Full path of the config file; created (seeded from the user's NuGet settings) on first access.</summary>
    string ConfigFilePath { get; }

    /// <summary>Every source in file order, enabled or not.</summary>
    IReadOnlyList<NuGetSource> Load();

    /// <summary>Replaces every source (order kept) and raises <see cref="Changed"/>.</summary>
    void Save(IReadOnlyList<NuGetSource> sources);

    event Action? Changed;
}
