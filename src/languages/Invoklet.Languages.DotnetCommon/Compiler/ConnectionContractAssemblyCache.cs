using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;
using Invoklet.Contracts.Connections;
using Microsoft.CodeAnalysis;

namespace Invoklet.Languages.DotnetCommon.Compiler;

/// <summary>Content-addressed assemblies for schema-generated types, independent of script text.</summary>
public static class ConnectionContractAssemblyCache
{
    // Partial additions in the script must share the generated types' compilation. A conservative
    // text check also covers incomplete edits; a false positive only takes the slower source path.
    public static bool RequiresSourceCompilation(string code) => code.Contains("partial", StringComparison.Ordinal);

    private static string PathFor(string folder, IReadOnlyList<GeneratedSourceFile> files,
        IEnumerable<string> references, string settings)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length));
            hash.AppendData(bytes);
        }
        Add("connection-contracts-v1");
        Add(typeof(Compilation).Assembly.GetName().Version!.ToString());
        Add(settings);
        foreach (var file in files.OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            Add(file.Name);
            Add(file.Text);
        }
        foreach (var path in references.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var info = new FileInfo(path);
            Add(Path.GetFullPath(path));
            Add(info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Add(info.LastWriteTimeUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        var key = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        return Path.Combine(folder, "connection-contracts", "Invoklet.ConnectionContracts." + key + ".dll");
    }

    public static string? Find(string folder, IReadOnlyList<GeneratedSourceFile> files,
        IEnumerable<string> references, string settings)
    {
        var path = PathFor(folder, files, references, settings);
        return IsValid(path) ? path : null;
    }

    private static bool IsValid(string path)
    {
        try
        {
            if (!File.Exists(Path.ChangeExtension(path, ".xml"))) return false;
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata) return false;
            var metadata = pe.GetMetadataReader();
            return metadata.IsAssembly && metadata.GetString(metadata.GetAssemblyDefinition().Name) == Path.GetFileNameWithoutExtension(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException) { return false; }
    }

    public static string GetOrCreate(string folder, IReadOnlyList<GeneratedSourceFile> files,
        IEnumerable<string> references, string settings, Func<string, Compilation> create, CancellationToken cancellationToken)
    {
        var path = PathFor(folder, files, references, settings);
        if (IsValid(path)) return path;
        cancellationToken.ThrowIfCancellationRequested();
        var compilation = create(Path.GetFileNameWithoutExtension(path));
        compilation = compilation.WithOptions(compilation.Options.WithOutputKind(OutputKind.DynamicallyLinkedLibrary));
        using var pe = new MemoryStream();
        using var xml = new MemoryStream();
        var result = compilation.Emit(pe, xmlDocumentationStream: xml, cancellationToken: cancellationToken);
        if (!result.Success)
            throw new ConnectionContractCompilationException(result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var xmlTemp = temp + ".xml";
        try
        {
            File.WriteAllBytes(temp, pe.ToArray());
            File.WriteAllBytes(xmlTemp, xml.ToArray());
            cancellationToken.ThrowIfCancellationRequested();
            // Publish the DLL last, so readers never see it without its documentation.
            Publish(xmlTemp, temp, path, (from, to) => File.Move(from, to, overwrite: true), delay => Thread.Sleep(delay));
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            try { File.Delete(xmlTemp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return path;
    }

    private const int PublishAttempts = 8;

    /// <summary>
    /// Moves the documentation and then the assembly into place. Concurrent builders of the same
    /// content overwrite the same two destinations; Windows reports such a collision as an
    /// <see cref="IOException"/> or an <see cref="UnauthorizedAccessException"/>, and the winner may
    /// have published only the documentation so far. A failed attempt is therefore retried a bounded
    /// number of times, ending as soon as the entry is complete; a failure that persists (a genuine
    /// permission or disk error) is rethrown.
    /// </summary>
    internal static void Publish(string xmlTemp, string temp, string path,
        Action<string, string> move, Action<int> wait)
    {
        var xmlPath = Path.ChangeExtension(path, ".xml");
        var xmlMoved = false;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                if (IsValid(path)) return;
                if (!xmlMoved)
                {
                    move(xmlTemp, xmlPath);
                    xmlMoved = true;
                }
                move(temp, path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (IsValid(path)) return; // Another builder published the same content.
                if (attempt == PublishAttempts) throw;
                wait(10 * attempt);
            }
        }
    }
}

public sealed class ConnectionContractCompilationException(IReadOnlyList<Diagnostic> diagnostics)
    : Exception("The connection contract could not be compiled.")
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; } = diagnostics;
}
