using System.Text;

namespace DotPad.Runtime;

// LINQPad-style live-updating dump target (ANALYSIS.md feature #19 / LINQPAD-5): Dump() it once,
// then set Content (or call Refresh()) as many times as needed and the SAME block in the output
// pane updates in place instead of appending a new one each time. Identified by a stable id
// embedded in the rendered HTML (data-dp-id) — DotPad's UI matches on that id to replace the
// existing block rather than appending (see EditorTabItemControl.RenderDump).
public sealed class DumpContainer
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private object? _content;
    private string? _label;

    public object? Content
    {
        get => _content;
        set { _content = value; Refresh(); }
    }

    public DumpContainer() { }
    public DumpContainer(object? content) => _content = content;

    public DumpContainer Dump(string? label = null)
    {
        _label = label;
        Emit();
        return this;
    }

    public void Refresh() => Emit();

    private void Emit()
    {
        var inner = DumpRenderer.Render(_content, _label, new DumpOptions());
        var html = $"<div data-dp-id=\"{_id}\">{inner}</div>";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(html));
        Console.WriteLine(ObjectExtensions.DumpLinePrefixChar + encoded);
    }
}
