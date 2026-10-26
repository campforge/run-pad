namespace RunPad.Format.RPad;

/// <summary>
/// Which arguments a run of a script gets, the same for the window and the command line:
/// <list type="number">
/// <item>arguments given for this run (the command line after the script), as they are;</item>
/// <item>otherwise the set named for this run (<c>--args-set</c>);</item>
/// <item>otherwise the script's selected set (<c>selectedArgumentSet:</c>);</item>
/// <item>otherwise its <c>args:</c>, or none.</item>
/// </list>
/// A set name the script doesn't have is an error, never a silent fallback to <c>args:</c>.
/// </summary>
public static class RpadArguments
{
    public static RpadArgumentsResult Resolve(RpadFrontMatter? frontMatter, IReadOnlyList<string>? given = null, string? setName = null)
    {
        if (given is not null) return new(given, null);
        var name = setName ?? frontMatter?.SelectedArgumentSet;
        if (name is null) return new(frontMatter?.Args ?? [], null);
        if (frontMatter is not null && frontMatter.ArgumentSets.TryGetValue(name, out var set)) return new(set, null);
        var known = frontMatter?.ArgumentSets.Keys.ToList() ?? [];
        return new([], known.Count == 0
            ? $"The script has no argument set '{name}': it defines none."
            : $"The script has no argument set '{name}'. Its sets: {string.Join(", ", known)}.");
    }
}

/// <summary>The arguments for a run, or why there are none: <see cref="Error"/> is set and the run must not start.</summary>
public sealed record RpadArgumentsResult(IReadOnlyList<string> Arguments, string? Error);
