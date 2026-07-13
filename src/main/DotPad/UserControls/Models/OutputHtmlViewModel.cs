using ReactiveUI;

namespace DotPad.UserControls.Models;

// Backs OutputHtmlView the same way FileTreeViewModel backs FileTreeView — the view binds to this
// instead of being pushed a fully-assembled HTML string imperatively. Css and Content are separate
// reactive properties (rather than one "SetContent(css, content)" call) because they change at
// wildly different rates: Css only once at startup and again on a theme switch, Content on every
// single line of script output — collapsing them into one call meant every write site had to pass
// the (almost always unchanged) Css back in just to satisfy the method signature.
public class OutputHtmlViewModel : ReactiveObject
{
    public string Css
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    public string Content
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";
}
