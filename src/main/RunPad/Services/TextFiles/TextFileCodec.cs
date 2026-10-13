using System.Globalization;
using System.Text;

namespace RunPad.Services.TextFiles;

/// <summary>A text file's content and how it's encoded on disk — what a save has to write back.</summary>
/// <param name="Text">The decoded text, line breaks exactly as in the file.</param>
/// <param name="Encoding">The encoding the bytes were decoded with (and are written back with).</param>
/// <param name="HasBom">Whether the file starts with a byte order mark (written back too).</param>
public sealed record TextFileContent(string Text, Encoding Encoding, bool HasBom)
{
    /// <summary>Status-bar text: "UTF-8", "UTF-8 with BOM", "UTF-16 LE", "windows-1251", ...</summary>
    public string EncodingLabel => TextFileCodec.Label(Encoding, HasBom);
}

/// <summary>
/// Loads and saves the text editor window's files
/// without changing how they're encoded: a BOM decides the encoding; without one, strict UTF-8 is
/// tried first, then the system ANSI code page on Windows (e.g. 1251 — Russian text files), else
/// Latin-1, whose byte-for-byte round trip is lossless. Line breaks are never touched — the text keeps
/// whatever the file had. Saving writes a temporary file next to the target and then replaces it, so
/// a failed write leaves the original intact.
/// </summary>
public static class TextFileCodec
{
    static TextFileCodec() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static async Task<TextFileContent> LoadAsync(string path, CancellationToken cancellationToken = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, cancellationToken);
        return Decode(bytes);
    }

    public static TextFileContent Decode(byte[] bytes)
    {
        foreach (var (preamble, encoding) in BomEncodings)
        {
            if (bytes.AsSpan().StartsWith(preamble))
                return new TextFileContent(encoding.GetString(bytes, preamble.Length, bytes.Length - preamble.Length), encoding, HasBom: true);
        }

        try
        {
            return new TextFileContent(StrictUtf8.GetString(bytes), StrictUtf8, HasBom: false);
        }
        catch (DecoderFallbackException)
        {
            var fallback = FallbackEncoding();
            return new TextFileContent(fallback.GetString(bytes), fallback, HasBom: false);
        }
    }

    public static byte[] Encode(string text, Encoding encoding, bool hasBom)
    {
        var body = encoding.GetBytes(text);
        if (!hasBom) return body;
        var preamble = encoding.GetPreamble();
        return [.. preamble, .. body];
    }

    /// <summary>
    /// Writes <paramref name="text"/> to <paramref name="path"/> in <paramref name="encoding"/>: into a
    /// temporary file in the same folder first, which then replaces the target. Throws (IOException,
    /// UnauthorizedAccessException, ...) without having touched an existing file when the write fails.
    /// </summary>
    public static async Task SaveAsync(string path, string text, Encoding encoding, bool hasBom, CancellationToken cancellationToken = default)
    {
        var bytes = Encode(text, encoding, hasBom);
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temp = Path.Combine(folder, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temp, bytes, cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>"CRLF", "LF", "CR", "Mixed", or "" for text without any line break.</summary>
    public static string NewLineLabel(string text)
    {
        int crlf = 0, lf = 0, cr = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r')
            {
                if (i + 1 < text.Length && text[i + 1] == '\n') { crlf++; i++; }
                else cr++;
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }

        var kinds = (crlf > 0 ? 1 : 0) + (lf > 0 ? 1 : 0) + (cr > 0 ? 1 : 0);
        return kinds switch
        {
            0 => "",
            > 1 => "Mixed",
            _ => crlf > 0 ? "CRLF" : lf > 0 ? "LF" : "CR",
        };
    }

    internal static string Label(Encoding encoding, bool hasBom) => encoding switch
    {
        UTF8Encoding => hasBom ? "UTF-8 with BOM" : "UTF-8",
        UnicodeEncoding { CodePage: 1201 } => "UTF-16 BE",
        UnicodeEncoding => "UTF-16 LE",
        UTF32Encoding => "UTF-32 LE",
        _ => encoding.WebName,
    };

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // longest preamble first: UTF-32 LE's FF FE 00 00 starts with UTF-16 LE's FF FE
    private static readonly (byte[] Preamble, Encoding Encoding)[] BomEncodings =
    [
        ([0xFF, 0xFE, 0x00, 0x00], new UTF32Encoding(bigEndian: false, byteOrderMark: true)),
        ([0xEF, 0xBB, 0xBF], new UTF8Encoding(encoderShouldEmitUTF8Identifier: true)),
        ([0xFF, 0xFE], new UnicodeEncoding(bigEndian: false, byteOrderMark: true)),
        ([0xFE, 0xFF], new UnicodeEncoding(bigEndian: true, byteOrderMark: true)),
    ];

    // Windows: the system's ANSI code page (what Notepad wrote non-UTF-8 files in); elsewhere Latin-1,
    // which maps every byte to one character and back, so an untouched byte is never altered on save.
    private static Encoding FallbackEncoding()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                // fall through to Latin-1
            }
        }
        return Encoding.Latin1;
    }
}
