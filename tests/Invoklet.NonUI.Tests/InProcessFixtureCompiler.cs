using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Invoklet.Languages.CSharp;

namespace Invoklet.Runtime.UI.Tests;

// CSharpDecompilerServiceTests/IlDisassemblerTests need a REAL, valid managed assembly on disk — the
// thing under test is whether ICSharpCode.Decompiler can read actual IL, which a hand-mocked PE file
// can't stand in for — but their source snippets are self-contained (no Invoklet.Runtime/.Dump()
// dependency), so a real `dotnet build` child process is unnecessary overhead for what they're
// proving. CSharpReferenceService.GetDefault() already gives Roslyn everything it needs to compile
// and emit a real DLL entirely in-process (no MSBuild/dotnet CLI, no restore, no child process at
// all) — same "real Roslyn, not mocked" principle this codebase already leans on for
// CSharpIntelliSenseService's own AdhocWorkspace.
internal static class InProcessFixtureCompiler
{
    /// <param name="resources">Embedded manifest resources (name, data), public — the explorer's Resources node.</param>
    public static string CompileToTempDll(
        string code, string assemblyName, IReadOnlyList<(string Name, byte[] Data)>? resources = null)
    {
        var tree = CSharpSyntaxTree.ParseText(code);
        var references = new CSharpReferenceService().GetDefault()
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path));
        var compilation = CSharpCompilation.Create(
            assemblyName,
            [tree],
            references,
            new CSharpCompilationOptions(OutputKind.ConsoleApplication));

        var outputPath = Path.Combine(Path.GetTempPath(), $"{assemblyName}-{Guid.NewGuid()}.dll");
        var result = compilation.Emit(outputPath, manifestResources: resources?.Select(r =>
            new ResourceDescription(r.Name, () => new MemoryStream(r.Data), isPublic: true)));
        if (!result.Success)
        {
            throw new InvalidOperationException(
                "Fixture compilation failed:\n" + string.Join('\n', result.Diagnostics.Select(d => d.ToString())));
        }

        return outputPath;
    }
}
