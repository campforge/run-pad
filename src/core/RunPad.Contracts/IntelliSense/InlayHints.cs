namespace RunPad.Contracts.IntelliSense;

// TODO.md #62 "inlay hints" — a single inferred-type annotation to render inline in the editor (the
// dim "int" in "var int x = 5;", Rider/VS Code style) — decorative only, Text is never part of the
// actual document. Offset is where to insert it (code-relative, same convention as every other
// IIntelliSenseService result — see IDiagnosticInfo/ISemanticToken).
public interface IInlayHint
{
    int Offset { get; }
    string Text { get; }
}
