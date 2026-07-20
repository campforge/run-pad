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

/// <summary>Raised when a running script calls Util.ReadLine/GetPassword (TODO.md #63) — see <see cref="DotPad.Contracts.ILanguageHost.InputRequested"/>.</summary>
public delegate void InputRequestedHandler(string? prompt, bool isPassword);
