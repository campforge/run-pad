namespace RunPad.Contracts.IntelliSense;

public interface IQuickInfoResult
{
    // The hovered symbol's signature, classified for syntax highlighting.
    IReadOnlyList<DisplayTextSegment> Signature { get; }

    // Plain-text XML-doc <summary>, or null if the symbol has none.
    string? Summary { get; }
}
