namespace Invoklet.Services.QuickOpen;

/// <summary>What the Quick Open box is given: the entries it starts with and how to get the rest on demand.</summary>
/// <param name="Entries">The scripts and the samples.</param>
/// <param name="LoadOtherFilesAsync">The files of other types; asked for only when the user includes them.</param>
/// <param name="ReadTextAsync">The text of an entry, for "search inside"; null when it cannot be read.</param>
/// <param name="KeyText">The key that opens the box, as the settings have it; shown in the title.</param>
public sealed record QuickOpenContent(
    IReadOnlyList<QuickOpenEntry> Entries,
    Func<CancellationToken, Task<IReadOnlyList<QuickOpenEntry>>> LoadOtherFilesAsync,
    Func<QuickOpenEntry, CancellationToken, Task<string?>> ReadTextAsync,
    string KeyText = "");
