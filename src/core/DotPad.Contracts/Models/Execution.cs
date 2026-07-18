namespace DotPad.Contracts.Models;

public enum OutputKind
{
    StandardOutput,
    StandardError,
    DumpHtml,
}

public delegate void OutputReceivedHandler(string data, OutputKind kind);

/// <summary>Raised after a successful compile with the path to the compiled assembly on disk — see <see cref="DotPad.Contracts.ILanguageHost.Compiled"/>.</summary>
public delegate void CompiledHandler(string binaryPath);
