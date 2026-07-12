namespace DotPad.Contracts.IntelliSense;

public interface ISignatureHelpResult
{
    IReadOnlyList<ISignatureHelpItem> Items { get; }
    int SelectedItemIndex { get; }
    int ArgumentIndex { get; }
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
