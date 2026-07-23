using System.Globalization;
using System.Text;
using System.Text.Json;

// ReSharper disable once CheckNamespace

/// <summary>
/// Interactive input from the output panel (TODO.md #63) — LINQPad-compatible <c>Util.ReadLine</c>/
/// <c>Util.GetPassword</c>. Blocks the calling script until the user answers, right where <c>.Dump()</c>
/// output appears. Uses the same sentinel-prefixed stdout protocol as <c>ObjectExtensions.Dump</c>
/// (see <see cref="InputRequestPrefixChar"/>, a different prefix so the two don't collide) to ask for
/// input, paired with a plain-text reply over stdin — CompiledDotnetLanguageHostBase/
/// EditorTabItemControl wire both directions up on the host side. Only meaningful under
/// <c>RunAsync</c> (the plain, non-debug run) — a debug session's stdin isn't currently piped through.
/// </summary>
public static class Util
{
    internal const char InputRequestPrefixChar = '\x1f'; // ASCII Unit Separator — distinct from ObjectExtensions.DumpLinePrefixChar

    // TODO.md #111 (bucket 1) — mirrors ChildProcess.CurrentQueryPathEnvVar (DotPad.Build.Core), a
    // separate assembly this one (netstandard2.0, referenced by the compiled SCRIPT's own project,
    // not by DotPad itself) has no reference to — duplicated literal, not shared, same precedent as
    // InputRequestPrefixChar/RsPrefix mirroring across the same process boundary.
    internal const string CurrentQueryPathEnvVar = "DOTPAD_CURRENT_QUERY_PATH";

    /// <summary>
    /// The full path to the running script's own <c>.rpad</c> file, or <see langword="null"/> if it's
    /// never been saved (an untitled tab) — LINQPad-compatible <c>Util.CurrentQueryPath</c>. Works
    /// under both a plain run and a debug session (DapSession forwards it via the launch request's
    /// own <c>env</c>, since netcoredbg spawns the debuggee itself rather than DotPad).
    /// </summary>
    public static string? CurrentQueryPath => Environment.GetEnvironmentVariable(CurrentQueryPathEnvVar);

    // TODO.md #111 (bucket 1) — LINQPad-compatible Util.SaveString/LoadString/SaveBytes/LoadBytes save
    // to a dedicated per-user data folder, not an arbitrary path the caller has to come up with —
    // mirrors the OS-special-folder + "DotPad" subfolder convention DotPad's own
    // PreferencesService/BuildCacheService already use for their own data (a further "UserData"
    // level under that so scripts' own files don't collide with either), duplicated rather than
    // shared since this assembly (netstandard2.0, referenced by the compiled SCRIPT's own project)
    // has no reference to DotPad's main app project. fileName may itself contain subdirectories
    // (e.g. "cache/foo.json") — created on demand by the Save* half, same as LINQPad's own behavior.
    private static string ResolveUserDataPath(string fileName)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DotPad", "UserData");
        var path = Path.Combine(root, fileName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }

    /// <summary>Writes <paramref name="text"/> to <paramref name="fileName"/> in DotPad's per-user data folder (created on demand), overwriting any existing content.</summary>
    public static void SaveString(string text, string fileName) => File.WriteAllText(ResolveUserDataPath(fileName), text);

    /// <summary>Reads back a file previously written by <see cref="SaveString"/> — throws the usual <see cref="IOException"/> family if it doesn't exist.</summary>
    public static string LoadString(string fileName) => File.ReadAllText(ResolveUserDataPath(fileName));

    /// <summary>Writes <paramref name="data"/> to <paramref name="fileName"/> in DotPad's per-user data folder (created on demand), overwriting any existing content.</summary>
    public static void SaveBytes(byte[] data, string fileName) => File.WriteAllBytes(ResolveUserDataPath(fileName), data);

    /// <summary>Reads back a file previously written by <see cref="SaveBytes"/> — throws the usual <see cref="IOException"/> family if it doesn't exist.</summary>
    public static byte[] LoadBytes(string fileName) => File.ReadAllBytes(ResolveUserDataPath(fileName));

    /// <summary>
    /// Runs <paramref name="action"/>, returning any exception it throws instead of letting it
    /// propagate (<see langword="null"/> if it completed without throwing) — LINQPad-inspired
    /// <c>Util.Try</c>. No decompiled reference for this one's exact original signature was available
    /// to check against (see TODO.md #111's own note on this bucket being "по построению", not a
    /// byte-for-byte port); this is a straightforward, self-contained try/catch wrapper only.
    /// </summary>
    public static Exception? Try(Action action)
    {
        try
        {
            action();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>Same as <see cref="Try(Action)"/>, but for a function that returns a value — <c>Result</c> is <see langword="default"/> if <paramref name="func"/> threw.</summary>
    public static (T? Result, Exception? Exception) Try<T>(Func<T> func)
    {
        try
        {
            return (func(), null);
        }
        catch (Exception ex)
        {
            return (default, ex);
        }
    }

    /// <summary>Reads a single line of text typed into the output panel. <see langword="null"/> if the run was cancelled/stdin closed before an answer arrived.</summary>
    public static string? ReadLine(string? prompt = null) => ReadLineCore(prompt, isPassword: false);

    /// <summary>Same as <see cref="ReadLine(string?)"/>, but the output panel masks what's typed — still plain text on the wire, just not shown on screen.</summary>
    public static string? GetPassword(string? prompt = null) => ReadLineCore(prompt, isPassword: true);

    /// <summary>
    /// Reads a line and converts it to <typeparamref name="T"/> via <see cref="Convert.ChangeType(object, Type)"/>
    /// (works for <see cref="int"/>, <see cref="double"/>, <see cref="bool"/>, <see cref="DateTime"/>,
    /// any other <see cref="IConvertible"/> target type). <see langword="default"/> if the run was
    /// cancelled/stdin closed before an answer arrived.
    /// </summary>
    public static T? ReadLine<T>(string? prompt = null)
    {
        var text = ReadLineCore(prompt, isPassword: false);
        return text is null ? default : (T)Convert.ChangeType(text, typeof(T), CultureInfo.InvariantCulture);
    }

    private static string? ReadLineCore(string? prompt, bool isPassword)
    {
        var json = JsonSerializer.Serialize(new { Prompt = prompt, IsPassword = isPassword });
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        Console.WriteLine(InputRequestPrefixChar + encoded);
        return Console.In.ReadLine();
    }
}
