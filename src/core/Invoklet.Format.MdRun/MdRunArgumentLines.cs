namespace Invoklet.Format.MdRun;

/// <summary>
/// An argument list as editable text, one argument per line exactly as it reaches <c>args</c>:
/// spaces and quotes are part of the argument, an empty line is an empty argument. One final line
/// break is not an argument of its own, so "a\n" and "a" are the same list. An argument that
/// contains a line break has no line of its own; <see cref="Fits"/> says whether a list can be shown.
/// </summary>
public static class MdRunArgumentLines
{
    public static List<string> Parse(string text)
    {
        if (text.Length == 0) return [];
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    // A list that ends with an empty argument gets one more line break: the final one isn't an argument.
    public static string Format(IReadOnlyList<string> arguments)
        => string.Join("\n", arguments) + (arguments.Count > 0 && arguments[^1].Length == 0 ? "\n" : "");

    /// <summary>Whether <see cref="Parse"/> of <see cref="Format"/> gives the same list back: no argument has a line break.</summary>
    public static bool Fits(IReadOnlyList<string> arguments) => !arguments.Any(a => a.Contains('\n') || a.Contains('\r'));
}
