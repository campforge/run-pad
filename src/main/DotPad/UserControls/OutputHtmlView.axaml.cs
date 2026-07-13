using System.Reactive.Disposables;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using DotPad.UserControls.Models;
using ReactiveUI;

namespace DotPad.UserControls;

// Hosts the script's Results output (TODO.md #72) — binds to an OutputHtmlViewModel set as its
// DataContext (see EditorsTabItemControl's constructor: ResultsOutput.DataContext = State.Output)
// rather than being pushed content imperatively. Assembling Css/Content into the actual HTML
// document (<html><head><style>...</style></head><body>...</body></html>) and registering the
// custom output font are both a concern of this control, not the editor tab.
public partial class OutputHtmlView : UserControl
{
    private CompositeDisposable? _modelSubscription;
    private bool _applyScheduled;

    public OutputHtmlView()
    {
        InitializeComponent();

        OutputHtml.Container.AddFontFamily(
            new FontFamily(new Uri("avares://dotpad/Assets/fonts/CustomFont.ttf"), "1 Smoothy DNA"));

        DataContextChanged += (_, _) => Rebind();
    }

    private void Rebind()
    {
        _modelSubscription?.Dispose();
        _modelSubscription = null;

        if (DataContext is not OutputHtmlViewModel vm) return;

        // WhenAnyValue emits the current value immediately on subscribe, so binding to a
        // freshly-created OutputHtmlViewModel schedules an initial apply for free — no separate
        // "push the starting state" call needed.
        _modelSubscription = new CompositeDisposable(
            vm.WhenAnyValue(x => x.Css).Subscribe(_ => ScheduleApply()),
            vm.WhenAnyValue(x => x.Content).Subscribe(_ => ScheduleApply()));
    }

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
        if (DataContext is not OutputHtmlViewModel vm) return;

        ApplyCount++;
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
}
