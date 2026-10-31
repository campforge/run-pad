namespace Invoklet.Services.QuickOpen;

/// <summary>The "search inside" of Quick Open: which scripts contain the text that was typed.</summary>
public static class QuickOpenContentSearch
{
    public const int DefaultLimit = 100;

    /// <param name="candidates">What to look in.</param>
    /// <param name="query">The text to find, in any case.</param>
    /// <param name="readText">The text of an entry; null when it cannot be read, and then it is skipped.</param>
    /// <param name="limit">Stops looking after this many matches.</param>
    public static async Task<IReadOnlyList<QuickOpenEntry>> FindAsync(
        IEnumerable<QuickOpenEntry> candidates, string query, Func<QuickOpenEntry, CancellationToken, Task<string?>> readText,
        CancellationToken ct, int limit = DefaultLimit)
    {
        query = query.Trim();
        var found = new List<QuickOpenEntry>();
        if (query.Length == 0) return found;

        foreach (var entry in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var text = await readText(entry, ct);
            if (text is null || text.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;
            found.Add(entry);
            if (found.Count >= limit) break;
        }
        return found;
    }
}
