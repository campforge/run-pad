namespace Invoklet.Contracts.IntelliSense;

/// <summary>A change to the text of the editor: <paramref name="Length"/> characters from <paramref name="Start"/> become <paramref name="NewText"/>.</summary>
public sealed record TextEdit(int Start, int Length, string NewText);
