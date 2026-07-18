using System.Text;
using ReactiveUI;

namespace DotPad.UserControls.Models;

// Backs OutputHtmlView the same way FileTreeViewModel backs FileTreeView — the view binds to this
// instead of being pushed a fully-assembled HTML string imperatively. Css and Content are separate
// (rather than one "SetContent(css, content)" call) because they change at wildly different rates:
// Css only once at startup and again on a theme switch, Content on every single line of script
// output — collapsing them into one call meant every write site had to pass the (almost always
// unchanged) Css back in just to satisfy the method signature.
public class OutputHtmlViewModel : ReactiveObject
{
    public string Css
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = "";

    // ANALYSIS.md #94: a script dumping thousands of lines used to call `Content += line` once per
    // line — each `+=` copies the ENTIRE accumulated string so far, making total cost O(n²) in the
    // number of lines. Backed by a StringBuilder instead; Append below is amortized O(1) per call,
    // same as the accumulation pattern it replaces was meant to look like at the call site.
    private readonly StringBuilder _content = new();

    // Read-only from the outside — mutate via Append/Replace/Clear, not direct assignment, so every
    // write actually goes through the StringBuilder above instead of silently reintroducing the
    // O(n²) concatenation this type exists to avoid. OutputHtmlView deliberately does NOT observe
    // this property reactively (that would call the ToString() below on every single Append) — it
    // subscribes to the raw PropertyChanged event instead and only reads Content once the already
    // -throttled apply actually runs.
    public string Content => _content.ToString();

    public void Append(string html)
    {
        _content.Append(html);
        this.RaisePropertyChanged(nameof(Content));
    }

    // For DumpContainer's "update this specific block in place" (EditorTabItemControl.RenderDump) —
    // StringBuilder.Replace still walks the buffer to find oldValue, same cost as string.Replace,
    // but at least the surrounding Append calls (the overwhelming majority of writes) are no longer
    // quadratic.
    public void Replace(string oldValue, string newValue)
    {
        _content.Replace(oldValue, newValue);
        this.RaisePropertyChanged(nameof(Content));
    }

    public void Clear()
    {
        _content.Clear();
        this.RaisePropertyChanged(nameof(Content));
    }
}
