namespace Invoklet.Services.QuickOpen;

/// <summary>What the Quick Open box shows and which row is selected: the results for the text typed so far, and moving through them.</summary>
public sealed class QuickOpenSession(IReadOnlyList<QuickOpenEntry> entries)
{
    private IReadOnlyList<QuickOpenEntry> _results = QuickOpenMatcher.Rank(entries, "");

    public IReadOnlyList<QuickOpenEntry> Results => _results;

    /// <summary>The selected row, or -1 when nothing matches.</summary>
    public int SelectedIndex { get; private set; } = entries.Count > 0 ? 0 : -1;

    public QuickOpenEntry? Selected => SelectedIndex >= 0 ? _results[SelectedIndex] : null;

    /// <summary>A new text: the best match is selected.</summary>
    public void SetQuery(string query)
    {
        _results = QuickOpenMatcher.Rank(entries, query);
        SelectedIndex = _results.Count > 0 ? 0 : -1;
    }

    /// <summary>Moves the selection by <paramref name="delta"/> rows and stops at the ends.</summary>
    public void Move(int delta)
    {
        if (_results.Count == 0) return;
        SelectedIndex = Math.Clamp(SelectedIndex + delta, 0, _results.Count - 1);
    }

    /// <summary>Selects a row the user pointed at; a row that is not there is ignored.</summary>
    public void Select(int index)
    {
        if (index >= 0 && index < _results.Count) SelectedIndex = index;
    }
}
