using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;

namespace DotPad.Languages.DotnetCommon;

/// <summary>
/// Disassembles a compiled assembly to raw IL text — backs the "IL" output tab (TODO.md #62,
/// LINQPad has the same feature). Driven off <see cref="Contracts.ILanguageHost.Compiled"/>, so this
/// only ever runs against a binary that has already finished building.
/// </summary>
public static class IlDisassembler
{
    public static string Disassemble(string assemblyPath)
    {
        // PEFile.Dispose() releases the FileStream it opens on assemblyPath internally (PEReader ->
        // FileStream) — without this, the handle outlives the call on any OS that enforces exclusive
        // file locks (Windows; POSIX allows unlinking/rebuilding an open file, so this never surfaced
        // there — live Windows CI report, 2026-07-19). That alone only covers a SUCCESSFUL
        // construction, though — PEFile's convenience constructor opens the FileStream inline as part
        // of a chained constructor call, so a file that fails to parse as a valid PE image (this
        // method only ever runs against a binary the compiler just produced, so unlikely here in
        // practice — see CSharpDecompilerService.WithDecompiler's own copy of this comment for where
        // it actually got caught) would leak that stream regardless, since the exception happens
        // before `using var peFile = ...` ever completes its assignment. Opening the stream ourselves
        // first gives it its own `using`, independent of whether PEFile's own constructor throws.
        using var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read);
        using var peFile = new PEFile(assemblyPath, stream);
        var output = new PlainTextOutput();
        var disassembler = new ReflectionDisassembler(output, CancellationToken.None);
        disassembler.WriteModuleContents(peFile);
        return output.ToString();
    }
}
