namespace RunPad.Format.RPad;

/// <summary>How a <c>references:</c> entry is written, judged from its text alone.</summary>
public enum ReferencePathKind
{
    /// <summary>Relative to the script's folder, e.g. <c>lib/A.dll</c>.</summary>
    Relative,
    /// <summary>Rooted on a Unix system, e.g. <c>/opt/lib/A.dll</c>.</summary>
    Rooted,
    /// <summary>A Windows drive path, <c>C:\Lib\A.dll</c> or <c>C:/Lib/A.dll</c>.</summary>
    WindowsDrive,
    /// <summary>A Windows network path, <c>\\server\share\A.dll</c> or <c>//server/share/A.dll</c>.</summary>
    WindowsUnc,
    /// <summary><c>C:lib/A.dll</c>: relative to the current directory of a drive, which can't be known.</summary>
    WindowsDriveRelative,
}

/// <summary>
/// The spelling of <c>references:</c> entries. Written paths use <c>/</c> on every system so a
/// script moves between Windows, macOS and Linux unchanged; Windows spellings are still read.
/// Only separators are ever changed — never case, whitespace or macros — and on Unix a backslash
/// in a relative or rooted path stays as it is, since there it is a file-name character.
/// </summary>
public static class ReferencePath
{
    public const string CurrentQueryPathMacro = "$(CurrentQueryPath)";

    public static ReferencePathKind Classify(string reference)
    {
        if (reference.Length >= 2 && IsSeparator(reference[0]) && IsSeparator(reference[1]))
            return ReferencePathKind.WindowsUnc;
        if (reference.Length >= 2 && char.IsAsciiLetter(reference[0]) && reference[1] == ':')
            return reference.Length >= 3 && IsSeparator(reference[2])
                ? ReferencePathKind.WindowsDrive
                : ReferencePathKind.WindowsDriveRelative;
        return reference.StartsWith('/') ? ReferencePathKind.Rooted : ReferencePathKind.Relative;
    }

    /// <summary>
    /// The form a reference is saved in: <c>/</c> separators for a Windows drive or network path on
    /// any system, and for every path when <paramref name="windowsHost"/> (where <c>\</c> can only be
    /// a separator). Idempotent.
    /// </summary>
    public static string Canonical(string reference, bool windowsHost)
    {
        if (windowsHost) return reference.Replace('\\', '/');
        return Classify(reference) is ReferencePathKind.WindowsDrive or ReferencePathKind.WindowsUnc
            ? reference.Replace('\\', '/')
            : reference;
    }

    /// <summary>
    /// The same reference for a script saved in <paramref name="newDirectory"/> instead of
    /// <paramref name="oldDirectory"/>: a relative path (or one starting with
    /// <c>$(CurrentQueryPath)</c>) is rewritten to reach the same file from the new folder; a path on
    /// another Windows volume becomes absolute. Anything else — rooted, other macros, Windows paths —
    /// names the same file from anywhere and is returned unchanged, as is everything when the folder
    /// stays the same.
    /// </summary>
    public static string Rebase(string reference, string oldDirectory, string newDirectory, bool windowsHost)
    {
        if (PathsEqual(oldDirectory, newDirectory, windowsHost)) return reference;

        string prefix;
        string relative;
        if (reference.StartsWith(CurrentQueryPathMacro, StringComparison.Ordinal))
        {
            prefix = CurrentQueryPathMacro + "/";
            relative = reference[CurrentQueryPathMacro.Length..].TrimStart(SeparatorsFor(windowsHost));
        }
        else if (reference.StartsWith("$(", StringComparison.Ordinal) || Classify(reference) != ReferencePathKind.Relative)
        {
            return reference;
        }
        else
        {
            prefix = "";
            relative = reference;
        }

        var target = Path.GetFullPath(Path.Combine(oldDirectory, relative));
        var rebased = Path.GetRelativePath(newDirectory, target);
        // Another volume: no relative path reaches it, so the absolute one is written.
        if (Path.IsPathRooted(rebased)) prefix = "";
        return prefix + Canonical(rebased, windowsHost);
    }

    private static bool IsSeparator(char c) => c is '/' or '\\';

    private static char[] SeparatorsFor(bool windowsHost) => windowsHost ? ['/', '\\'] : ['/'];

    private static bool PathsEqual(string a, string b, bool windowsHost) =>
        string.Equals(Path.GetFullPath(a).TrimEnd(SeparatorsFor(windowsHost)), Path.GetFullPath(b).TrimEnd(SeparatorsFor(windowsHost)),
            windowsHost ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
