namespace DotPad.Contracts.IntelliSense;

public interface ISignatureHelpResult
{
    IReadOnlyList<ISignatureHelpItem> Items { get; }
    int SelectedItemIndex { get; }
    int ArgumentIndex { get; }
}

public interface ISignatureHelpItem
{
    string DisplaySignature { get; }
    string? Documentation { get; }
    IReadOnlyList<ISignatureHelpParameter> Parameters { get; }
}

public interface ISignatureHelpParameter
{
    string Name { get; }
    string DisplayParts { get; }
    string? Documentation { get; }
}
