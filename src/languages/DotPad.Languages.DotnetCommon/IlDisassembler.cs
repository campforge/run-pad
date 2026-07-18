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
        var peFile = new PEFile(assemblyPath);
        var output = new PlainTextOutput();
        var disassembler = new ReflectionDisassembler(output, CancellationToken.None);
        disassembler.WriteModuleContents(peFile);
        return output.ToString();
    }
}
