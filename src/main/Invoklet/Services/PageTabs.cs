namespace Invoklet.Services;

/// <summary>
/// The tabs of a viewer whose every tab shows one page at a time and has its own Back and Forward history, as in a browser:
/// a new page puts the current one on the Back stack and drops Forward; going back or forward swaps the current page with
/// the top of the other stack. There is always at least one tab; the last one is emptied instead of closed. A page is a
/// record the viewer defines; before a page is left, the viewer gets to <c>remember</c> where it was (the caret line).
/// </summary>
internal sealed class PageTabs<TPage> where TPage : class
{
    private readonly List<Tab> _tabs = [new()];

    public PageTabs() => Active = _tabs[0];

    /// <summary>One tab: the page it shows and its two histories.</summary>
    public sealed class Tab
    {
        public TPage? Current { get; set; }

        public Stack<TPage> Back { get; } = new();

        public Stack<TPage> Forward { get; } = new();
    }

    public Tab Active { get; private set; }

    public IReadOnlyList<Tab> All => _tabs;

    public int Count => _tabs.Count;

    public int ActiveIndex => _tabs.IndexOf(Active);

    public bool Contains(Tab tab) => _tabs.Contains(tab);

    public bool CanGoBack => Active.Back.Count > 0;

    public bool CanGoForward => Active.Forward.Count > 0;

    /// <summary>A new empty tab right of the active one. It is not activated.</summary>
    public Tab InsertAfterActive()
    {
        var tab = new Tab();
        _tabs.Insert(ActiveIndex + 1, tab);
        return tab;
    }

    /// <summary>Makes the tab the active one, after the active one has remembered its page; false for a tab that is not in the list.</summary>
    public bool Activate(Tab tab, Func<TPage, TPage> remember)
    {
        if (!_tabs.Contains(tab)) return false;
        if (Active.Current is { } leaving && _tabs.Contains(Active)) Active.Current = remember(leaving);
        Active = tab;
        return true;
    }

    /// <summary>
    /// Closes the tab. The last tab is emptied instead, and is the one to activate again; closing the active tab hands over the
    /// neighbour that takes its place; closing another one needs nothing to be activated.
    /// </summary>
    public (bool Found, Tab? ToActivate) Close(Tab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return (false, null);
        if (_tabs.Count == 1)
        {
            tab.Current = null;
            tab.Back.Clear();
            tab.Forward.Clear();
            return (true, tab);
        }

        _tabs.RemoveAt(index);
        return (true, tab == Active ? _tabs[Math.Min(index, _tabs.Count - 1)] : null);
    }

    /// <summary>The tab <paramref name="step"/> places after the active one, wrapping around (a negative step goes left).</summary>
    public Tab Next(int step) => _tabs[((ActiveIndex + step) % _tabs.Count + _tabs.Count) % _tabs.Count];

    /// <summary>A new page in the active tab: the current one goes onto the Back stack and Forward is dropped.</summary>
    public void Show(TPage page, Func<TPage, TPage> remember)
    {
        if (Active.Current is { } current)
        {
            Active.Back.Push(remember(current));
            Active.Forward.Clear();
        }

        Active.Current = page;
    }

    /// <summary>Goes back in the active tab; the page to show, or null when there is nothing to go back to.</summary>
    public TPage? GoBack(Func<TPage, TPage> remember)
    {
        if (Active.Current is not { } current || !Active.Back.TryPop(out var page)) return null;
        Active.Forward.Push(remember(current));
        Active.Current = page;
        return page;
    }

    /// <summary>Goes forward in the active tab; the page to show, or null when there is nothing to go forward to.</summary>
    public TPage? GoForward(Func<TPage, TPage> remember)
    {
        if (Active.Current is not { } current || !Active.Forward.TryPop(out var page)) return null;
        Active.Back.Push(remember(current));
        Active.Current = page;
        return page;
    }

    /// <summary>Replaces every page of every tab and history, keeping the order of the stacks.</summary>
    public void Rewrite(Func<TPage, TPage> change)
    {
        foreach (var tab in _tabs)
        {
            Rewrite(tab.Back, change);
            Rewrite(tab.Forward, change);
            tab.Current = tab.Current is null ? null : change(tab.Current);
        }
    }

    private static void Rewrite(Stack<TPage> stack, Func<TPage, TPage> change)
    {
        var pages = stack.Reverse().Select(change).ToList();
        stack.Clear();
        foreach (var page in pages) stack.Push(page);
    }
}
