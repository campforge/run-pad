using Invoklet.Services;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

internal sealed class PageTabsFixture
{
    /// <summary>A page of a viewer: what it shows and where the caret was.</summary>
    public sealed record Page(string Title, int CaretLine = 1);

    /// <summary>What the viewer does before a page is left: it remembers the caret line.</summary>
    public static Func<Page, Page> Remember(int caretLine) => page => page with { CaretLine = caretLine };

    public static readonly Func<Page, Page> Unchanged = page => page;

    public PageTabs<Page> CreateSut() => new();
}
