using System.Text;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.Util;

namespace Invoklet.Languages.DotnetCommon;

/// <summary>
/// A manifest resource of an assembly (the explorer's Resources node). Only an embedded resource can
/// be read; a linked one lives in another file. <see cref="Size"/> is null when it can't be told.
/// </summary>
public sealed record AssemblyResourceEntry(string Name, long? Size, bool IsEmbedded, bool IsPublic);

public static partial class CSharpDecompilerService
{
    // What a resource page reads at most — enough for any text/.resources worth reading in a code pane.
    private const int MaxResourceBytes = 4 * 1024 * 1024;
    private const int HexDumpBytes = 1024;

    private static IReadOnlyList<AssemblyResourceEntry> ResourcesOf(MetadataFile file) => file.Resources
        .Select(r => new AssemblyResourceEntry(
            r.Name, r.TryGetLength(), r.ResourceType == ResourceType.Embedded,
            (r.Attributes & System.Reflection.ManifestResourceAttributes.VisibilityMask)
            == System.Reflection.ManifestResourceAttributes.Public))
        .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();

    /// <summary>
    /// The explorer's page for one resource, under a "// name (size)" header: a <c>.resources</c> file
    /// as <c>key = value</c> lines, text as text, anything else as a hex dump of its start. A linked
    /// resource (stored outside the assembly) or an unreadable one gets an explanation instead.
    /// </summary>
    public static DecompiledMemberResult DescribeResource(string assemblyPath, string resourceName)
    {
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        using var peFile = new PEFile(assemblyPath, stream);
        var resource = peFile.Resources.FirstOrDefault(r => r.Name == resourceName);
        var text = new StringBuilder();
        if (resource is null)
        {
            text.Append($"// Resource '{resourceName}' was not found in '{peFile.Name}'.\n");
            return new DecompiledMemberResult(text.ToString(), 1, resourceName);
        }

        var length = resource.TryGetLength();
        text.Append($"// {resource.Name}{(length is { } n ? $" ({FormatSize(n)})" : "")}\n");
        if (resource.ResourceType != ResourceType.Embedded)
        {
            text.Append("// A linked resource: its data is stored outside this assembly.\n");
            return new DecompiledMemberResult(text.ToString(), 1, resourceName);
        }

        using var data = resource.TryOpenStream();
        if (data is null)
        {
            text.Append("// The resource data could not be read.\n");
            return new DecompiledMemberResult(text.ToString(), 1, resourceName);
        }

        var bytes = ReadAtMost(data, MaxResourceBytes, out var truncated);
        text.Append('\n');
        if (resourceName.EndsWith(".resources", StringComparison.OrdinalIgnoreCase) && TryAppendResourcesFile(bytes, text))
        {
            // done
        }
        else if (DecodeText(bytes) is { } decoded)
        {
            text.Append(decoded);
            if (truncated) text.Append($"\n\n// ... truncated after {FormatSize(MaxResourceBytes)}.\n");
        }
        else
        {
            AppendHexDump(bytes.AsSpan(0, Math.Min(bytes.Length, HexDumpBytes)), text);
            if (bytes.Length > HexDumpBytes || truncated)
                text.Append($"// ... the first {HexDumpBytes} bytes are shown.\n");
        }
        return new DecompiledMemberResult(text.ToString(), 1, resourceName);
    }

    private static byte[] ReadAtMost(Stream stream, int max, out bool truncated)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < max && (read = stream.Read(chunk, 0, (int)Math.Min(chunk.Length, max - buffer.Length))) > 0)
            buffer.Write(chunk, 0, read);
        truncated = stream.ReadByte() >= 0;
        return buffer.ToArray();
    }

    // A .resources file: one "key = value" line per entry. False when it isn't a valid one.
    private static bool TryAppendResourcesFile(byte[] bytes, StringBuilder text)
    {
        var lines = new List<string>();
        try
        {
            using var resources = new ResourcesFile(new MemoryStream(bytes), leaveOpen: false);
            foreach (var (key, value) in resources)
                lines.Add($"{key} = {FormatResourceValue(value)}");
        }
        catch (Exception ex) when (ex is BadImageFormatException or IOException or InvalidDataException
                                       or ArgumentException or NotSupportedException or EndOfStreamException)
        {
            return false;
        }

        text.Append($"// {lines.Count} {(lines.Count == 1 ? "entry" : "entries")}\n");
        foreach (var line in lines.OrderBy(l => l, StringComparer.Ordinal))
            text.Append(line).Append('\n');
        return true;
    }

    private static string FormatResourceValue(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n")
                         .Replace("\t", "\\t") + "\"",
        byte[] b => $"byte[{b.Length}]",
        bool or char or IFormattable => $"{value} ({value.GetType().Name})",
        _ => value.ToString() ?? value.GetType().Name,
    };

    // Text if it has a Unicode BOM, or no NUL bytes and decodes as strict UTF-8; otherwise null (binary).
    private static string? DecodeText(byte[] bytes)
    {
        if (bytes is [0xFF, 0xFE, ..] || bytes is [0xFE, 0xFF, ..] || bytes is [0xEF, 0xBB, 0xBF, ..])
        {
            using var reader = new StreamReader(new MemoryStream(bytes), detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        if (Array.IndexOf(bytes, (byte)0) >= 0) return null;
        try
        {
            return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return null;
        }
    }

    // "00000000  4D 5A 90 00 ...  |MZ..|", 16 bytes a line.
    private static void AppendHexDump(ReadOnlySpan<byte> bytes, StringBuilder text)
    {
        for (var offset = 0; offset < bytes.Length; offset += 16)
        {
            var line = bytes.Slice(offset, Math.Min(16, bytes.Length - offset));
            text.Append(offset.ToString("X8")).Append("  ");
            for (var i = 0; i < 16; i++)
                text.Append(i < line.Length ? line[i].ToString("X2") + " " : "   ");
            text.Append(" |");
            foreach (var b in line)
                text.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
            text.Append("|\n");
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
