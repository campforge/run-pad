namespace DotPad.Contracts.Models;

public enum OutputKind
{
    StandardOutput,
    StandardError,
    DumpHtml,
}

public delegate void OutputReceivedHandler(string data, OutputKind kind);
