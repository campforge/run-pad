using System.Text.RegularExpressions;

namespace Invoklet.Format.MdRun;

/// <summary>
/// Which arguments a run of a script gets, the same for the window and the command line:
/// <list type="number">
/// <item>arguments given for this run (the command line after the script), as they are;</item>
/// <item>otherwise each declared argument (<c>arguments:</c>) as <c>--name value</c>, with the value
/// of the set named for this run (<c>--args-set</c>) or the selected set (<c>selectedArgumentSet:</c>),
/// else its default.</item>
/// </list>
/// Every problem is reported at once: a set the script doesn't have, a value that isn't of its
/// argument's type, a set value for an argument that isn't declared. With any, the run must not start.
/// </summary>
public static partial class MdRunArguments
{
    public static MdRunArgumentsResult Resolve(MdRunFrontMatter? frontMatter, IReadOnlyList<string>? given = null, string? setName = null)
    {
        if (given is not null) return new(given, null);
        if (frontMatter is null) return new([], null);

        // The declarations and the set this run uses: a mistake in another set doesn't stop it.
        var errors = new List<string>(ValidateDeclarations(frontMatter, out var declared));
        Dictionary<string, MdRunValue>? set = null;
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
            MdRunValue? value = null;
            if (set is null || !set.TryGetValue(argument.Name, out value)) value = argument.Default;
            if (value is null) continue;
            foreach (var item in MdRunArgument.Items(value))
                arguments.AddRange(["--" + argument.Name, item]);
        }
        return errors.Count == 0 ? new(arguments, null) : new([], string.Join("\n", errors));
    }

    /// <summary>What is wrong with the script's declared arguments and all of its sets, every problem once.</summary>
    public static IReadOnlyList<string> Validate(MdRunFrontMatter frontMatter)
    {
        var errors = ValidateDeclarations(frontMatter, out var declared);
        foreach (var (setName, values) in frontMatter.ArgumentSets) errors.AddRange(ValidateSet(setName, values, declared));
        return errors;
    }

    internal static IReadOnlyList<string> ValidateSchema(MdRunFrontMatter frontMatter)
        => ValidateDeclarations(frontMatter, out _);

    private static List<string> ValidateDeclarations(MdRunFrontMatter frontMatter, out Dictionary<string, MdRunArgument> declared)
    {
        var errors = new List<string>();
        declared = new Dictionary<string, MdRunArgument>(StringComparer.Ordinal);
        var generatedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var argument in frontMatter.Arguments)
        {
            if (!ValidName().IsMatch(argument.Name))
                errors.Add($"'{argument.Name}' can't be an argument name: use letters, digits and _, starting with a letter.");
            else if (!declared.TryAdd(argument.Name, argument))
                errors.Add($"The argument '{argument.Name}' is declared twice.");
            else if (!generatedNames.TryAdd(PropertyName(argument.Name), argument.Name))
                errors.Add($"The argument '{argument.Name}' conflicts with '{generatedNames[PropertyName(argument.Name)]}' in the generated Arguments contract.");
            if (argument.Type == MdRunArgumentType.Enum && argument.Values.Count == 0)
                errors.Add($"'{argument.Name}' is an enum without values.");
            if (argument.Type == MdRunArgumentType.Enum)
            {
                var enumNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var enumType = "Arguments" + PropertyName(argument.Name);
                foreach (var valueName in argument.Values)
                {
                    var identifier = MdRunArgumentContractMember.EnumMemberName(valueName);
                    if (!enumNames.Add(identifier) || string.Equals(identifier, enumType, StringComparison.OrdinalIgnoreCase))
                        errors.Add($"'{argument.Name}' enum value '{valueName}' conflicts with the generated enum name '{identifier}'.");
                }
            }
            if (argument.Default is { } value && argument.Check(value) is { } problem)
                errors.Add("Default: " + problem);
        }
        return errors;
    }

    private static string PropertyName(string name) => name.Length == 0
        ? name
        : char.ToUpperInvariant(name[0]) + name[1..];

    private static IEnumerable<string> ValidateSet(string setName, Dictionary<string, MdRunValue> values,
        Dictionary<string, MdRunArgument> declared)
    {
        foreach (var (name, value) in values)
        {
            if (!declared.TryGetValue(name, out var argument))
                yield return $"The set '{setName}' gives '{name}', which is not a declared argument.";
            else if (argument.Check(value) is { } problem)
                yield return $"In the set '{setName}': {problem}";
        }
    }

    // What a generated Arguments member and a --name option can both be.
    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_]*$")]
    private static partial Regex ValidName();
}

/// <summary>The arguments for a run, or why there are none: <see cref="Error"/> is set and the run must not start.</summary>
public sealed record MdRunArgumentsResult(IReadOnlyList<string> Arguments, string? Error);
