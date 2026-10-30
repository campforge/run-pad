using Invoklet.Contracts.Models;

namespace Invoklet.Languages.DotnetCommon.Compiler;

public interface IDotnetProcessCompiler : IDisposable
{
    /// <summary>Raised as the compile moves through its stages; a compiler that reports none never raises it.</summary>
    event StageChangedHandler? StageChanged
    {
        add { }
        remove { }
    }

    Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx);
}

public record ProcessCompilerResult(
    string OutputBinaryFile,
    bool Success,
    IReadOnlyList<ResultItem> Errors,
    IReadOnlyList<ResultItem> Warnings);

public record LineSymbol(uint Line, uint Symbol);
public record ResultItem(string Code, string File, LineSymbol LineSymbol, string Message);
