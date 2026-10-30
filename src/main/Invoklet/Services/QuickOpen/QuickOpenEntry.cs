using System.Globalization;

namespace Invoklet.Services.QuickOpen;

/// <summary>What a row of Quick Open opens.</summary>
public enum QuickOpenKind
{
    /// <summary>A script of the user's.</summary>
    Script,

    /// <summary>A sample script bundled with the application; it opens as an untitled copy.</summary>
    Sample,

    /// <summary>A file of another type in the scripts folder; it opens in the text editor.</summary>
    OtherFile,
}

/// <summary>A script, sample or file that Quick Open can open.</summary>
/// <param name="Kind">What the row is.</param>
/// <param name="Target">The full path of a script or other file; the id of a sample.</param>
/// <param name="Name">What the user types against and reads first: the file name without its extension, or the title of a sample.</param>
/// <param name="Folder">Where it is, shown beside the name: relative to the scripts folder, or the full folder of a file outside it; the folder of a sample.</param>
/// <param name="RecentRank">0 for the file opened last, up from there; -1 when it is not among the recent files.</param>
/// <param name="Modified">When the file was last written; null for a sample.</param>
public sealed record QuickOpenEntry(QuickOpenKind Kind, string Target, string Name, string Folder, int RecentRank, DateTime? Modified = null)
{
    /// <summary>The last write time as the "Last Modified" column shows it; empty when there is none.</summary>
    public string ModifiedText => Modified?.ToString("g", CultureInfo.CurrentCulture) ?? "";
}
