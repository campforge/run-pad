namespace Invoklet.Runtime;

/// <summary>
/// LINQPad-compatible live-updating dump target (signature verified against the real
/// <c>LINQPad.Runtime.dll</c>): <c>Dump()</c> it once, then call
/// <see cref="UpdateContent{T}(T, bool)"/>/<see cref="AppendContent{T}(T, bool)"/>/<see cref="ClearContent"/>
/// (or just set <see cref="Content"/>) as many times as needed and the SAME block in the output pane
/// updates in place instead of appending a new one each time. Identified by a stable id embedded in
/// the rendered HTML (<c>data-dp-id</c>) — Invoklet's UI matches on that id to replace the existing
/// block rather than appending (see <c>EditorTabItemControl.RenderDump</c>).
/// </summary>
/// <remarks>
/// Deliberately has no <c>Dump()</c> method of its own — the real type doesn't either. An instance
/// method named <c>Dump</c> would SHADOW every <see cref="ObjectExtensions.Dump{T}(T, string?, string?)"/>
/// extension overload for callers of this specific type (C# stops looking for extension-method
/// candidates the moment it finds ANY instance member with a matching name, even one whose parameters
/// don't otherwise match) — silently breaking <c>dc.Dump(3)</c>/<c>dc.Dump(new DumpOptions())</c>/etc.
/// <c>DumpRenderer.RenderValue</c> instead special-cases this type (the same way it already does for
/// <see cref="ICustomMemberProvider"/>) so every normal <c>.Dump()</c> overload keeps working on it.
/// </remarks>
public sealed class DumpContainer
{
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly SimpleObservable<object?> _contentChanged = new();
    private readonly SimpleObservable<object?> _contentAppended = new();
    private readonly SimpleObservable<string?> _styleChanged = new();
    private object? _content;
    private string? _style;
    private string _appendedHtml = "";
    private bool _dumped;
    // Captured from the FIRST .Dump() call's own (already GlobalDefaults-merged) options and reused
    // for every later re-render — see ContentOptions()/RenderForFirstDump's own comment for why this
    // has to be a field rather than reconstructed fresh each time.
    private DumpOptions _dumpOptions = new();

    /// <summary>Creates an empty container — set <see cref="Content"/> once ready to render.</summary>
    public DumpContainer() { }

    /// <summary>Creates a container already holding <paramref name="initialContent"/>.</summary>
    public DumpContainer(object? initialContent) => _content = initialContent;

    /// <summary>The value currently rendered. Setting it re-renders in place and raises <see cref="ContentChanged"/> — same as calling <see cref="UpdateContent{T}(T, bool)"/>.</summary>
    public object? Content
    {
        get => _content;
        set => UpdateContent(value);
    }

    /// <summary>Inline CSS applied to this container's own wrapping element. Setting it re-renders in place and raises <see cref="StyleChanged"/>.</summary>
    public string? Style
    {
        get => _style;
        set
        {
            _style = value;
            StyleChanged?.Invoke(this, EventArgs.Empty);
            _styleChanged.Publish(value);
            RefreshIfDumped();
        }
    }

    /// <summary>Same depth budget as <see cref="ObjectExtensions.Dump{T}(T, int, int?)"/>, applied to every future render of this container's own content. <see langword="null"/> uses Invoklet's built-in default.</summary>
    public int? DumpDepth { get; set; }

    /// <summary>Raised whenever <see cref="Content"/>/<see cref="UpdateContent{T}(T, bool)"/> replaces the content.</summary>
    public event EventHandler? ContentChanged;

    /// <summary>Raised whenever <see cref="AppendContent{T}(T, bool)"/> adds to the content.</summary>
    public event Action<object?>? ContentAppended;

    /// <summary>Raised whenever <see cref="Style"/> changes.</summary>
    public event EventHandler? StyleChanged;

    /// <summary>
    /// Replaces the current content with <paramref name="newContent"/> (discarding anything
    /// previously appended via <see cref="AppendContent{T}(T, bool)"/>), re-rendering in place, and
    /// returns it unchanged (so this chains the same way <c>.Dump()</c> does). <paramref name="scrollIntoView"/>
    /// is accepted for LINQPad-signature compatibility — Invoklet's output pane doesn't currently act on it.
    /// </summary>
    public T UpdateContent<T>(T newContent, bool scrollIntoView = false)
    {
        _content = newContent;
        _appendedHtml = "";
        ContentChanged?.Invoke(this, EventArgs.Empty);
        _contentChanged.Publish(newContent);
        RefreshIfDumped();
        return newContent;
    }

    /// <summary>
    /// Renders <paramref name="toAppend"/> after the current content instead of replacing it, and
    /// returns it unchanged. <paramref name="inline"/> is accepted for LINQPad-signature
    /// compatibility — Invoklet always appends as its own block regardless.
    /// </summary>
    public T AppendContent<T>(T toAppend, bool inline = false)
    {
        ContentAppended?.Invoke(toAppend);
        _contentAppended.Publish(toAppend);
        _appendedHtml += DumpRenderer.Render(toAppend, null, ContentOptions());
        RefreshIfDumped();
        return toAppend;
    }

    /// <summary>Clears the current content (both anything set via <see cref="Content"/> and anything appended via <see cref="AppendContent{T}(T, bool)"/>) and re-renders in place, empty.</summary>
    public void ClearContent()
    {
        _content = null;
        _appendedHtml = "";
        ContentChanged?.Invoke(this, EventArgs.Empty);
        _contentChanged.Publish(null);
        RefreshIfDumped();
    }

    /// <summary>Fires every time <see cref="Content"/>/<see cref="UpdateContent{T}(T, bool)"/> replaces the content — Rx-compatible alternative to subscribing <see cref="ContentChanged"/>.</summary>
    public IObservable<object?> WhenContentChanged() => _contentChanged;

    /// <summary>Fires every time <see cref="AppendContent{T}(T, bool)"/> adds to the content — Rx-compatible alternative to subscribing <see cref="ContentAppended"/>.</summary>
    public IObservable<object?> WhenContentAppended() => _contentAppended;

    /// <summary>Fires every time <see cref="Style"/> changes — Rx-compatible alternative to subscribing <see cref="StyleChanged"/>.</summary>
    public IObservable<string?> WhenStyleChanged() => _styleChanged;

    /// <summary>Re-renders the current content in place. A no-op until this container has actually been dumped once.</summary>
    public void Refresh() => RefreshIfDumped();

    private void RefreshIfDumped()
    {
        if (!_dumped) return;
        var html = BuildWrapperHtml();
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(html));
        Console.WriteLine(ObjectExtensions.DumpLinePrefixChar + encoded);
    }

    // Called by DumpRenderer.RenderValue for the FIRST render, reached through the normal .Dump()
    // extension-method pipeline (which does its own single Console.WriteLine once RenderValue
    // returns — this must NOT also write to the console itself, or the first dump would double up).
    // Marks this container "live" so every later Content/UpdateContent/AppendContent/ClearContent/
    // Style change starts self-emitting via RefreshIfDumped instead of silently doing nothing. Also
    // captures the options this .Dump() call resolved to (already GlobalDefaults-merged) so later
    // re-renders keep using them too, instead of losing everything except DumpDepth — a real bug this
    // fixed: DumpDepth previously only took effect starting with the SECOND render, since this method
    // used to just pass `options` straight through, ignoring DumpDepth entirely on the first one.
    internal string RenderForFirstDump(DumpOptions options)
    {
        _dumped = true;
        _dumpOptions = options;
        return BuildWrapperHtml();
    }

    // DumpDepth overrides MaxDepth specifically (it's this container's own, always-current property —
    // a script can change it after the fact and expects the NEXT render to reflect it); every other
    // option carries over unchanged from the original .Dump() call.
    private DumpOptions ContentOptions() => DumpDepth is { } depth ? _dumpOptions with { MaxDepth = depth } : _dumpOptions;

    private string BuildWrapperHtml()
    {
        var inner = DumpRenderer.Render(_content, null, ContentOptions()) + _appendedHtml;
        var styleAttr = _style is null ? "" : $" style=\"{DumpRenderer.Encode(_style)}\"";
        return $"<div data-dp-id=\"{_id}\"{styleAttr}>{inner}</div>";
    }

    // Minimal hand-rolled IObservable<T> — WhenContentChanged/WhenContentAppended/WhenStyleChanged
    // need to BE IObservable<T> for LINQPad-signature compatibility (IObservable<T>/IObserver<T>
    // themselves are plain BCL interfaces, no package needed), but this library is referenced by
    // every compiled script's own project — pulling in the full System.Reactive package just for a
    // tiny publish/subscribe list would add a real dependency to every single script build for a
    // feature most scripts never touch.
    private sealed class SimpleObservable<T> : IObservable<T>
    {
        private readonly List<IObserver<T>> _observers = [];

        public IDisposable Subscribe(IObserver<T> observer)
        {
            lock (_observers) _observers.Add(observer);
            return new Subscription(this, observer);
        }

        public void Publish(T value)
        {
            IObserver<T>[] snapshot;
            lock (_observers) snapshot = [.. _observers];
            foreach (var observer in snapshot) observer.OnNext(value);
        }

        private sealed class Subscription(SimpleObservable<T> owner, IObserver<T> observer) : IDisposable
        {
            public void Dispose() { lock (owner._observers) owner._observers.Remove(observer); }
        }
    }
}
