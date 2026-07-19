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
