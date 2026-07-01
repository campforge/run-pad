namespace DotPad.Contracts.Models;

public enum OutputKind
{
    StandardOutput,
    StandardError,
}

public delegate void OutputReceivedHandler(string data, OutputKind kind);
