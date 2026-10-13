namespace RunPad.Contracts.IntelliSense;

public interface ISignatureHelpResult
{
    IReadOnlyList<ISignatureHelpItem> Items { get; }
    int SelectedItemIndex { get; }
    int ArgumentIndex { get; }

    // True when no argument expression has been typed yet at ArgumentIndex — i.e. the
    // caret sits right after the call's `(` or a `,`, with nothing (or only whitespace) typed since.
    // EditorTabItemControl uses this to redirect an explicit-invoke completion request (Ctrl+Space)
    // to overloads instead of a plain completion list — a fresh argument slot is what Ctrl+Space is
    // really asking about there, matching Rider/VS, while a partially-typed argument still gets
    // normal completion for what's being typed.
    bool IsArgumentSlotEmpty { get; }
}

public interface ISignatureHelpItem
{
    // The full method signature, classified for syntax highlighting. When this item is the
    // currently-selected overload, the parameter at ISignatureHelpResult.ArgumentIndex is marked
    // with DisplayTextSegment.Emphasis so the UI can bold it, the way LINQPad/VS Code do.
    IReadOnlyList<DisplayTextSegment> Signature { get; }

    // Plain-text XML-doc <summary> for the method, or null if it has none.
    string? Summary { get; }
    IReadOnlyList<ISignatureHelpParameter> Parameters { get; }
}

public interface ISignatureHelpParameter
{
    string Name { get; }
    IReadOnlyList<DisplayTextSegment> Signature { get; }

    // Plain-text XML-doc <param name="..."> for this parameter, or null if it has none.
    string? Summary { get; }
}
