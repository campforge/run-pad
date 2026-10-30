using Invoklet.Format.MdRun;

namespace Invoklet.Services;

/// <summary>One package of the draft: a name and the version the script would use.</summary>
internal sealed class DraftPackage(string id, string version)
{
    public string Id { get; } = id;

    public string Version { get; set; } = version;
}

/// <summary>
/// The packages of a script as the dependencies dialog edits them, apart from the script until the user applies them:
/// the rules of adding, updating and removing (a package is never listed twice under names differing only by case, a
/// blank version is refused), how the draft differs from the front matter and how it is copied back into it, keeping the
/// <see cref="Package"/> object of an entry whose version did not change.
/// </summary>
internal sealed class DependencyDraft
{
    private readonly List<DraftPackage> _packages = [];

    public IReadOnlyList<DraftPackage> Packages => _packages;

    /// <summary>Starts from the packages of the front matter, in its order; none for null.</summary>
    public void Load(MdRunFrontMatter? frontMatter)
    {
        _packages.Clear();
        if (frontMatter?.Packages is not { } packages) return;
        foreach (var (id, package) in packages) _packages.Add(new DraftPackage(id, package.Version));
    }

    public DraftPackage? Find(string id) => _packages.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds the package; false (nothing changed) for a blank version or a package already there, whatever its case.</summary>
    public bool Add(string id, string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || Find(id) is not null) return false;
        _packages.Add(new DraftPackage(id, version.Trim()));
        return true;
    }

    /// <summary>Gives the package the version; false for a blank version or a package that is not there.</summary>
    public bool Update(string id, string? version)
    {
        if (string.IsNullOrWhiteSpace(version) || Find(id) is not { } package) return false;
        package.Version = version.Trim();
        return true;
    }

    public bool Remove(string id)
    {
        if (Find(id) is not { } package) return false;
        _packages.Remove(package);
        return true;
    }

    /// <summary>Whether the packages (their names, their versions and their order) differ from those of the front matter.</summary>
    public bool DiffersFrom(MdRunFrontMatter frontMatter)
    {
        var original = frontMatter.Packages?.ToList() ?? [];
        return original.Count != _packages.Count
               || original.Zip(_packages).Any(p => p.First.Key != p.Second.Id || p.First.Value.Version != p.Second.Version);
    }

    /// <summary>Replaces the packages of the front matter with the draft's; an entry whose version is unchanged keeps its own object.</summary>
    public void ApplyTo(MdRunFrontMatter frontMatter)
    {
        var previous = frontMatter.Packages is null
            ? new Dictionary<string, Package>()
            : new Dictionary<string, Package>(frontMatter.Packages);
        var packages = frontMatter.Packages ??= new Dictionary<string, Package>();
        packages.Clear();
        foreach (var draft in _packages)
            packages[draft.Id] = previous.TryGetValue(draft.Id, out var old) && old.Version == draft.Version
                ? old
                : new Package(draft.Version);
    }

    /// <summary>Copies the draft's values into the original list, which stays the same object; null stays null when there is nothing.</summary>
    public static List<string>? CopyBack(List<string>? original, IEnumerable<string> draft)
    {
        var values = draft.ToList();
        if (original is null && values.Count == 0) return null;
        original ??= [];
        original.Clear();
        original.AddRange(values);
        return original;
    }
}
