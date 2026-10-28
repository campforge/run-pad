using System.Diagnostics;

namespace RunPad.Desktop.Linux;

/// <summary>
/// KDE window rules that make KWin remember where RunPad's main window was and centre its dialogs.
/// On native Wayland only the compositor knows and sets window positions, so RunPad cannot do either.
/// </summary>
/// <remarks>
/// The rules are RunPad's own groups in ~/.config/kwinrulesrc, listed in [General] rules=; the user's
/// other rules are left exactly as they are.
/// </remarks>
internal static class KdeWindowRule
{
    internal const string Group = "runpad-remember-position";
    internal const string DialogGroup = "runpad-center-dialogs";

    // The main window's title, which only it has.
    private const string MainWindowTitle = "RunPad";

    private static string RulesPath => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config
            ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "kwinrulesrc");

    /// <summary>Running under KDE, whose KWin is what reads the rule.</summary>
    public static bool IsSupported =>
        OperatingSystem.IsLinux()
        && (Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "")
            .Split(':').Contains("KDE", StringComparer.OrdinalIgnoreCase);

    public static bool IsInstalled()
    {
        try
        {
            return File.Exists(RulesPath) && HasRule(File.ReadAllText(RulesPath));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Adds or removes the rule and asks KWin to reload its rules.</summary>
    public static void SetInstalled(bool installed)
        => Rewrite(text => installed ? AddRule(text) : RemoveRule(text));

    /// <summary>Brings a rule written by an older RunPad up to date; does nothing when there is none.</summary>
    public static void UpdateIfInstalled()
    {
        try
        {
            Rewrite(text => HasRule(text) ? AddRule(text) : text);
        }
        catch (Exception)
        {
            // The old rule keeps working, only less precisely.
        }
    }

    private static void Rewrite(Func<string, string> change)
    {
        var text = File.Exists(RulesPath) ? File.ReadAllText(RulesPath) : "";
        var updated = change(text);
        if (updated == text) return;

        Directory.CreateDirectory(Path.GetDirectoryName(RulesPath)!);
        var tmp = RulesPath + ".runpad.tmp";
        File.WriteAllText(tmp, updated);
        File.Move(tmp, RulesPath, overwrite: true);
        ReloadKWin();
    }

    internal static bool HasRule(string text)
        => ParseRuleList(text).Contains(Group) && text.Contains($"[{Group}]", StringComparison.Ordinal);

    internal static string AddRule(string text)
    {
        if (HasRule(text) && GroupLines(text, Group).Contains("titlematch=1")
            && ParseRuleList(text).Contains(DialogGroup))
            return text;

        var withoutStale = RemoveGroup(RemoveGroup(text, Group), DialogGroup);
        var rules = ParseRuleList(withoutStale).Where(r => r != Group && r != DialogGroup)
            .Append(Group).Append(DialogGroup).ToList();
        var result = SetGeneralKeys(withoutStale, rules);
        // KWin places a window when it is first committed, before Avalonia has told it which window a
        // dialog belongs to, so dialogs look like any other RunPad window then: the main window is told
        // apart by its exact title. positionrule=4 is "Remember"; placement=5 "Centered" and
        // placementrule=2 "Force"; wmclassmatch/titlematch 1 is an exact match, 3 a regular expression.
        return result.TrimEnd('\n') + "\n\n" +
            $"[{Group}]\n" +
            "Description=RunPad: remember window position\n" +
            "position=0,0\n" +
            "positionrule=4\n" +
            $"title={MainWindowTitle}\n" +
            "titlematch=1\n" +
            "wmclass=runpad\n" +
            "wmclassmatch=1\n" +
            "\n" +
            $"[{DialogGroup}]\n" +
            "Description=RunPad: center dialogs\n" +
            "placement=5\n" +
            "placementrule=2\n" +
            $"title=^(?!{MainWindowTitle}$)\n" +
            "titlematch=3\n" +
            "wmclass=runpad\n" +
            "wmclassmatch=1\n";
    }

    private static List<string> GroupLines(string text, string group)
    {
        var lines = new List<string>();
        var inGroup = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) inGroup = trimmed == $"[{group}]";
            else if (inGroup) lines.Add(trimmed);
        }
        return lines;
    }

    internal static string RemoveRule(string text)
    {
        var listed = ParseRuleList(text);
        if (!listed.Contains(Group) && !listed.Contains(DialogGroup)
            && !text.Contains($"[{Group}]", StringComparison.Ordinal)
            && !text.Contains($"[{DialogGroup}]", StringComparison.Ordinal))
            return text;
        var withoutGroups = RemoveGroup(RemoveGroup(text, Group), DialogGroup);
        return SetGeneralKeys(withoutGroups,
            ParseRuleList(withoutGroups).Where(r => r != Group && r != DialogGroup).ToList());
    }

    private static List<string> ParseRuleList(string text)
    {
        var general = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) general = trimmed == "[General]";
            else if (general && trimmed.StartsWith("rules=", StringComparison.Ordinal))
                return trimmed["rules=".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        return [];
    }

    private static string RemoveGroup(string text, string group)
    {
        var kept = new List<string>();
        var skipping = false;
        foreach (var line in text.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith('[')) skipping = trimmed == $"[{group}]";
            if (!skipping) kept.Add(line);
        }
        return string.Join('\n', kept);
    }

    // Rewrites rules= and count= in [General], creating the group or the keys when missing.
    private static string SetGeneralKeys(string text, List<string> rules)
    {
        var lines = text.Length == 0 ? [] : text.Split('\n').ToList();
        var start = lines.FindIndex(l => l.Trim() == "[General]");
        if (start < 0)
        {
            lines.InsertRange(0, ["[General]", "", ""]);
            start = 0;
        }

        var end = lines.FindIndex(start + 1, l => l.Trim().StartsWith('['));
        if (end < 0) end = lines.Count;
        for (var i = end - 1; i > start; i--)
        {
            if (lines[i].StartsWith("rules=", StringComparison.Ordinal)
                || lines[i].StartsWith("count=", StringComparison.Ordinal))
                lines.RemoveAt(i);
        }
        lines.Insert(start + 1, $"count={rules.Count}");
        lines.Insert(start + 2, $"rules={string.Join(',', rules)}");
        return string.Join('\n', lines);
    }

    private static void ReloadKWin()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("dbus-send",
                "--session --type=method_call --dest=org.kde.KWin /KWin org.kde.KWin.reconfigure")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            process?.WaitForExit(2000);
        }
        catch (Exception)
        {
            // KWin picks the rule up on its next start anyway.
        }
    }
}
