namespace Invoklet.Services;

/// <summary>What a Save As does with the file the user chose.</summary>
internal enum SaveAsOutcome
{
    /// <summary>The user cancelled the picker.</summary>
    Cancelled,

    /// <summary>The file would overwrite another version unasked, so nothing is written.</summary>
    Rejected,

    /// <summary>The tab's own file was chosen again: an in-place save, not a Save As.</summary>
    InPlace,

    /// <summary>The tab is written to the new file and rebound to it.</summary>
    Write,
}

/// <summary>The decisions about the files of tabs that are a function of paths, with the disk asked through a delegate.</summary>
internal static class TabFilePaths
{
    /// <summary>
    /// Decides a Save As. A copy of a file that has no file of its own (a recovered draft whose file was already open)
    /// never replaces that file, nor a file another tab has open: either would overwrite the other version unasked.
    /// </summary>
    /// <param name="chosenPath">What the picker returned; null when cancelled.</param>
    /// <param name="tabPath">The file of the tab; null for an untitled tab.</param>
    /// <param name="recoveredOriginalPath">The file a recovered draft was made from, if the tab is one.</param>
    /// <param name="anotherTabHasIt">Whether a tab other than this one is open on the chosen file.</param>
    public static SaveAsOutcome DecideSaveAs(
        string? chosenPath, string? tabPath, string? recoveredOriginalPath, bool anotherTabHasIt, StringComparison comparison)
    {
        if (chosenPath is null) return SaveAsOutcome.Cancelled;

        var chosen = Path.GetFullPath(chosenPath);
        if ((tabPath is null && recoveredOriginalPath is not null
                && string.Equals(Path.GetFullPath(recoveredOriginalPath), chosen, comparison)) || anotherTabHasIt)
            return SaveAsOutcome.Rejected;

        return tabPath is not null && string.Equals(chosen, Path.GetFullPath(tabPath), comparison)
            ? SaveAsOutcome.InPlace
            : SaveAsOutcome.Write;
    }

    /// <summary>The first free name for a copy next to the file: "name copy.ext", "name copy 2.ext", and so on.</summary>
    public static string NextCopyPath(string sourcePath, Func<string, bool> exists)
    {
        var folder = Path.GetDirectoryName(sourcePath)!;
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);

        for (var i = 1; ; i++)
        {
            var path = Path.Combine(folder, $"{name}{(i == 1 ? " copy" : $" copy {i}")}{extension}");
            if (!exists(path)) return path;
        }
    }
}
