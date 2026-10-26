using System.Text.RegularExpressions;

namespace RunPad.Format.RPad;

/// <summary>
/// Which arguments a run of a script gets, the same for the window and the command line:
/// <list type="number">
/// <item>arguments given for this run (the command line after the script), as they are;</item>
/// <item>otherwise each declared argument (<c>arguments:</c>) as <c>--name value</c>, with the value
/// of the set named for this run (<c>--args-set</c>) or the selected set (<c>selectedArgumentSet:</c>),
/// else its default, followed by <c>args:</c> as written.</item>
/// </list>
/// Every problem is reported at once: a set the script doesn't have, a value that isn't of its
/// argument's type, a set value for an argument that isn't declared. With any, the run must not start.
/// </summary>
public static partial class RpadArguments
{
    public static RpadArgumentsResult Resolve(RpadFrontMatter? frontMatter, IReadOnlyList<string>? given = null, string? setName = null)
    {
        if (given is not null) return new(given, null);
        if (frontMatter is null) return new([], null);

        // The declarations and the set this run uses: a mistake in another set doesn't stop it.
        var errors = new List<string>(ValidateDeclarations(frontMatter, out var declared));
        Dictionary<string, RpadValue>? set = null;
        var name = setName ?? frontMatter.SelectedArgumentSet;
        if (name is not null && !frontMatter.ArgumentSets.TryGetValue(name, out set))
        {
            var known = frontMatter.ArgumentSets.Keys.ToList();
            errors.Add(known.Count == 0
                ? $"The script has no argument set '{name}': it defines none."
                : $"The script has no argument set '{name}'. Its sets: {string.Join(", ", known)}.");
        }
        if (name is not null && set is not null) errors.AddRange(ValidateSet(name, set, declared));

        var arguments = new List<string>();
        foreach (var argument in frontMatter.Arguments)
        {
            RpadValue? value = null;
            if (set is null || !set.TryGetValue(argument.Name, out value)) value = argument.Default;
            if (value is null) continue;
            foreach (var item in RpadArgument.Items(value))
                arguments.AddRange(["--" + argument.Name, item]);
        }
        arguments.AddRange(frontMatter.Args ?? []);
        return errors.Count == 0 ? new(arguments, null) : new([], string.Join("\n", errors));
    }

    /// <summary>What is wrong with the script's declared arguments and all of its sets, every problem once.</summary>
    public static IReadOnlyList<string> Validate(RpadFrontMatter frontMatter)
    {
        var errors = ValidateDeclarations(frontMatter, out var declared);
        foreach (var (setName, values) in frontMatter.ArgumentSets) errors.AddRange(ValidateSet(setName, values, declared));
        return errors;
    }

    private static List<string> ValidateDeclarations(RpadFrontMatter frontMatter, out Dictionary<string, RpadArgument> declared)
    {
        var errors = new List<string>();
        declared = new Dictionary<string, RpadArgument>(StringComparer.Ordinal);
        foreach (var argument in frontMatter.Arguments)
        {
            if (!ValidName().IsMatch(argument.Name))
                errors.Add($"'{argument.Name}' can't be an argument name: use letters, digits and _, starting with a letter.");
            else if (!declared.TryAdd(argument.Name, argument))
                errors.Add($"The argument '{argument.Name}' is declared twice.");
            if (argument.Type == RpadArgumentType.Enum && argument.Values.Count == 0)
                errors.Add($"'{argument.Name}' is an enum without values.");
            if (argument.Default is { } value && argument.Check(value) is { } problem)
                errors.Add("Default: " + problem);
        }
        return errors;
    }

    private static IEnumerable<string> ValidateSet(string setName, Dictionary<string, RpadValue> values,
        Dictionary<string, RpadArgument> declared)
    {
        foreach (var (name, value) in values)
        {
            if (!declared.TryGetValue(name, out var argument))
                yield return $"The set '{setName}' gives '{name}', which is not a declared argument.";
            else if (argument.Check(value) is { } problem)
                yield return $"In the set '{setName}': {problem}";
        }
    }

    // What a generated Util.Args member and a --name option can both be.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ValidName();
}

/// <summary>The arguments for a run, or why there are none: <see cref="Error"/> is set and the run must not start.</summary>
public sealed record RpadArgumentsResult(IReadOnlyList<string> Arguments, string? Error);
