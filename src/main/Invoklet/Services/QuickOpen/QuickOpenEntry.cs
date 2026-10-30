namespace Invoklet.Services.QuickOpen;

/// <summary>A script that Quick Open can open.</summary>
/// <param name="Path">The full path of the file.</param>
/// <param name="Name">The file name without its extension: what the user types against and reads first.</param>
/// <param name="Folder">Where the file is, shown beside the name: relative to the scripts folder, or the full folder of a file outside it.</param>
/// <param name="RecentRank">0 for the file opened last, up from there; -1 when it is not among the recent files.</param>
public sealed record QuickOpenEntry(string Path, string Name, string Folder, int RecentRank);
