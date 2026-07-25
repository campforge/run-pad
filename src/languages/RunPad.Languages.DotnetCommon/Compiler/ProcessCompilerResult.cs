namespace RunPad.Languages.DotnetCommon.Compiler;

public interface IDotnetProcessCompiler : IDisposable
{
    Task<ProcessCompilerResult> Compilation(string code, CancellationToken ctx);
}

public record ProcessCompilerResult(
    string OutputBinaryFile,
    bool Success,
    IReadOnlyList<ResultItem> Errors,
    IReadOnlyList<ResultItem> Warnings);

public record LineSymbol(uint Line, uint Symbol);
public record ResultItem(string Code, string File, LineSymbol LineSymbol, string Message);
