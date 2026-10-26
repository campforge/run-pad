using System.Text.RegularExpressions;
using RunPad.Format.RPad;

namespace RunPad.Languages.VB;

/// <summary>
/// The code a Visual Basic script gets when it becomes a Program. A VB Program is compiled as written
/// (see VbLanguageHost's kind transform), so it needs the whole module: an empty script gets a
/// <c>Module Program</c> / <c>Sub Main()</c> skeleton, and a script of statements is wrapped in one.
/// Nothing changes when the script already declares a module or class. <c>${0}</c> marks the caret.
/// </summary>
internal static partial class VbProgramTemplate
{
    public const string Comment = "' Other methods and fields used by Main go here.";

    private const string DefaultIndent = "    ";

    public static string Skeleton { get; } = Wrap("", DefaultIndent);

    // <indent> is one level of the editor's indentation: Sub Main sits one level in, its body two.
    public static string? CodeForKind(string code, RpadKind kind, string indent = DefaultIndent)
    {
        if (kind != RpadKind.Program || DeclaresModuleOrClass().IsMatch(code)) return null;
        return Wrap(code, indent);
    }

    private static string Wrap(string code, string indent)
    {
        var body = code.Replace("\r\n", "\n").Trim('\n');
        var inMain = indent + indent;
        var lines = body.Length == 0
            ? [inMain + "${0}"]
            : body.Split('\n').Select((line, i) => (i == 0 ? inMain + "${0}" : line.Length == 0 ? "" : inMain) + line.TrimEnd());
        return $"Module Program\n{indent}Sub Main()\n{string.Join("\n", lines)}\n{indent}End Sub\n\n{indent}{Comment}\nEnd Module\n";
    }

    [GeneratedRegex(@"^\s*((Public|Friend|Partial)\s+)*(Module|Class|Structure)\s", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DeclaresModuleOrClass();
}
