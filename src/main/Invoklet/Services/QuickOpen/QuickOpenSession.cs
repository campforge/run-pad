namespace Invoklet.Services.QuickOpen;

/// <summary>What the Quick Open box shows and which row is selected: the entries the filters let through, ranked for the text typed so
/// far, and moving through them.</summary>
public sealed class QuickOpenSession
{
    private readonly List<QuickOpenEntry> _entries;
    private IReadOnlyList<QuickOpenEntry> _contentMatches = [];
    private string _query = "";
    private bool _showScripts = true;
    private bool _showSamples = true;
    private bool _includeOtherFiles;
    private bool _searchInside;

    public QuickOpenSession(IReadOnlyList<QuickOpenEntry> entries)
    {
        _entries = [.. entries];
        Results = [];
        Refresh();
    }

    public IReadOnlyList<QuickOpenEntry> Results { get; private set; }

    /// <summary>The selected row, or -1 when nothing matches.</summary>
    public int SelectedIndex { get; private set; }

    public QuickOpenEntry? Selected => SelectedIndex >= 0 ? Results[SelectedIndex] : null;

    public string Query => _query;

    public bool ShowScripts
    {
        get => _showScripts;
        set => Change(ref _showScripts, value);
    }

    public bool ShowSamples
    {
        get => _showSamples;
        set => Change(ref _showSamples, value);
    }

    public bool IncludeOtherFiles
    {
        get => _includeOtherFiles;
        set => Change(ref _includeOtherFiles, value);
    }

    public bool SearchInside
    {
        get => _searchInside;
        set => Change(ref _searchInside, value);
    }

    /// <summary>The entries that "search inside" would look in for the text typed: the ones the filters let through.</summary>
    public IReadOnlyList<QuickOpenEntry> ContentCandidates => [.. _entries.Where(Allowed)];

    /// <summary>A new text: the best match is selected, and the matches found in the contents for the old text are dropped.</summary>
    public void SetQuery(string query)
    {
        _query = query;
        _contentMatches = [];
        Refresh();
    }

    /// <summary>Adds entries that were not known at the start (the files of other types, once they have been listed).</summary>
    public void AddEntries(IEnumerable<QuickOpenEntry> entries)
    {
        _entries.AddRange(entries);
        Refresh();
    }

    /// <summary>The entries whose contents hold <paramref name="query"/>; ignored when the text has changed since the search began.</summary>
    public void SetContentMatches(string query, IReadOnlyList<QuickOpenEntry> matches)
    {
        if (query != _query) return;
        _contentMatches = matches;
        Refresh();
    }

    /// <summary>Moves the selection by <paramref name="delta"/> rows and stops at the ends.</summary>
    public void Move(int delta)
    {
        if (Results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, Results.Count - 1);
    }

    /// <summary>Selects a row the user pointed at; a row that is not there is ignored.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < Results.Count) SelectedIndex = index;
    }

    private bool Allowed(QuickOpenEntry entry) => entry.Kind switch
    {
        QuickOpenKind.Script => _showScripts,
        QuickOpenKind.Sample => _showSamples,
        _ => _includeOtherFiles,
    };

    private void Change(ref bool field, bool value)
    {
        if (field == value) return;
        field = value;
        Refresh();
    }

    private void Refresh()
    {
        var ranked = QuickOpenMatcher.Rank(_entries.Where(Allowed), _query);
        if (_searchInside && _query.Trim().Length > 0)
        {
            var named = new HashSet<QuickOpenEntry>(ranked);
            ranked = [.. ranked, .. _contentMatches.Where(Allowed).Where(e => !named.Contains(e))];
        }
        Results = ranked;
        SelectedIndex = ranked.Count > 0 ? 0 : -1;
    }
}
