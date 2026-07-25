using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DotPad.UserControls.Models;

namespace DotPad.UserControls;

// Hosts the script's Results output (TODO.md #72) — binds to an OutputHtmlViewModel set as its
// DataContext (see EditorsTabItemControl's constructor: ResultsOutput.DataContext = State.Output)
// rather than being pushed content imperatively. Assembling Css/Content into the actual HTML
// document (<html><head><style>...</style></head><body>...</body></html>) and registering the
// custom output font are both a concern of this control, not the editor tab.
public partial class OutputHtmlView : UserControl
{
    private OutputHtmlViewModel? _boundModel;
    private bool _applyScheduled;
    private bool _scrollToEndPending;

    public OutputHtmlView()
    {
        InitializeComponent();

        OutputHtml.Container.AddFontFamily(
            new FontFamily(new Uri("avares://dotpad/Assets/fonts/CustomFont.ttf"), "1 Smoothy DNA"));

        // TODO.md #108: no auto-scroll during streaming output — HtmlPanel's own TextProperty change
        // handler resets the scroll offset to (0,0) on every apply (see its OnPropertyChanged), so a
        // long-running script's output actually snapped back to the TOP on every appended line rather
        // than just "not scrolling". LayoutUpdated fires once the new (taller) content has actually
        // been measured/arranged — ScrollToEnd's own doc comment explains why it can't just be called
        // synchronously right after setting Text. _scrollToEndPending gates this to at most once per
        // ApplyPendingContent call, consumed by the very next layout pass that follows it.
        OutputHtml.LayoutUpdated += (_, _) =>
        {
            if (!_scrollToEndPending) return;
            _scrollToEndPending = false;
            OutputHtml.ScrollToEnd();
        };

        DataContextChanged += (_, _) => Rebind();
        // DataContext (State.Output, EditorTabItemControl.cs) is set exactly once per tab and never
        // reassigned — but switching tabs (either the outer file tabs or this control's own
        // Results/Debugger tabs, EditorTabItemControl.axaml) detaches this control from the visual
        // tree without ever touching DataContext. Without also rebinding here, a script that keeps
        // running while its tab isn't the visible one writes into a model nobody's listening to
        // anymore — output silently stops updating from the first tab switch onward (live bug
        // report: "переодически пропадает вывод в results если ... переключать вкладки").
        AttachedToVisualTree += (_, _) => Rebind();
        DetachedFromVisualTree += (_, _) => Unbind();
    }

    private void Rebind()
    {
        Unbind();

        if (DataContext is not OutputHtmlViewModel vm) return;

        _boundModel = vm;
        vm.PropertyChanged += OnModelPropertyChanged;

        // Plain PropertyChanged subscriptions don't replay on subscribe (unlike WhenAnyValue, used
        // here before ANALYSIS.md #94's fix) — schedule explicitly so binding to a freshly-created
        // OutputHtmlViewModel still shows its current state without a separate "push the starting
        // state" call.
        ScheduleApply();
    }

    private void Unbind()
    {
        if (_boundModel is not null) _boundModel.PropertyChanged -= OnModelPropertyChanged;
        _boundModel = null;
    }

    // Deliberately not evaluating e.PropertyName — Css and Content are the only two properties on
    // OutputHtmlViewModel, and both should trigger the same reapply. Crucially, this never touches
    // vm.Content itself (that would call OutputHtmlViewModel's ToString() on every single Append,
    // right back to the O(n²) cost ANALYSIS.md #94 removed) — only ApplyPendingContent reads it,
    // and only once per coalesced burst.
    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e) => ScheduleApply();

    // Content changes once per output line while a script is running (EditorTabItemControl's
    // AppendOutput sets State.Output.Content on every single line) — applying every single change
    // synchronously forces a full HTML reparse + layout + paint per call, which freezes the UI
    // thread on a script that dumps hundreds of lines in a tight loop. Instead this only ever
    // schedules at most one Background-priority apply: AppendOutput's writes happen on the
    // dispatcher's default priority, so while a burst of them is still queued the apply keeps
    // getting pre-empted, and only actually runs once the burst has drained — reading whatever the
    // model's latest Css/Content is at that point, not every intermediate state.
    private void ScheduleApply()
    {
        if (_applyScheduled) return;
        _applyScheduled = true;
        Dispatcher.UIThread.Post(ApplyPendingContent, DispatcherPriority.Background);
    }

    private void ApplyPendingContent()
    {
        _applyScheduled = false;
        if (_boundModel is not { } vm) return;

        ApplyCount++;
        _scrollToEndPending = true;
        OutputHtml.Text = $"""
                           <html>
                           <head><style>{vm.Css}</style></head>
                           <body>{vm.Content}</body>
                           </html>
                           """;
    }

    internal string? RenderedHtml => OutputHtml.Text;

    // Test-only observability for the coalescing above — counts real applies to the underlying
    // HtmlPanel, as opposed to how many times the bound model's Css/Content actually changed.
    internal int ApplyCount { get; private set; }

    // Test-only observability (InternalsVisibleTo) for the auto-scroll fix (TODO.md #108) — negative
    // Y means scrolled down from the top (HtmlContainer.ScrollOffset's own convention, mirrored by
    // HtmlPanel.ScrollToPoint: ScrollOffset = (-horizontalValue, -verticalValue)).
    internal Point ScrollOffsetForTests => OutputHtml.Container.ScrollOffset;
}
