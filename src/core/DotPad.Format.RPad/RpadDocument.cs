namespace DotPad.Format.RPad;

public sealed class RpadDocument
{
    public required RpadFrontMatter FrontMatter { get; init; }

    /// <summary>Code from the run block — the only part the user edits.</summary>
    public string RunCode { get; set; } = "";

    // Everything in the markdown body before the run-block code (including opening fence + newline)
    internal string BodyBeforeCode { get; init; } = "";
    // Everything in the markdown body from the closing ``` onwards
    internal string BodyAfterCode { get; init; } = "";
}
