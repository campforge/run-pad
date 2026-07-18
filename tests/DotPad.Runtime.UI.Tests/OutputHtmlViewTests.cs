using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DotPad.UserControls;
using DotPad.UserControls.Models;
using Xunit;

namespace DotPad.Runtime.UI.Tests;

// TODO.md #72: the <html><head><style>...</style></head><body>...</body></html> assembly used to
// live in EditorTabItemControl.UpdateOutputUi, directly against a bare HtmlPanel x:Name="OutputHtml"
// declared in its own .axaml — moved into this dedicated control, which now binds to an
// OutputHtmlViewModel (DataContext) rather than being pushed content through an imperative
// SetContent(css, content) call — see ANALYSIS.md's "Доследование" on the тридцать пятый проход for
// why: Css changes once at startup/theme switch, Content on every output line, and a single method
// taking both meant every write site had to pass the (almost always unchanged) Css back in.
public class OutputHtmlViewTests
{
    [AvaloniaFact]
    public async Task Binding_Should_WrapCssAndContent_IntoAnHtmlDocument()
    {
        var view = new OutputHtmlView();
        var vm = new OutputHtmlViewModel { Css = "body { color: red; }" };
        vm.Append("<div>hi</div>");

        view.DataContext = vm;
        await FlushPendingApply();

        Assert.Contains("<style>body { color: red; }</style>", view.RenderedHtml);
        Assert.Contains("<body><div>hi</div></body>", view.RenderedHtml);
    }

    // Follow-up after the user tried this live: a script dumping hundreds of lines in a tight loop
    // sets Content once per line (EditorTabItemControl.AppendOutput), and applying every single
    // change synchronously froze the UI thread. The view now coalesces — it only records that
    // *something* changed and applies at most once per burst.
    [AvaloniaFact]
    public async Task ContentChanges_Should_CoalesceManyRapidUpdates_IntoASingleApply()
    {
        var view = new OutputHtmlView();
        var vm = new OutputHtmlViewModel();
        view.DataContext = vm;
        await FlushPendingApply(); // let the initial bind's apply run before the burst below

        for (var i = 0; i < 1000; i++)
            vm.Append($"<div>{i}</div>");
        await FlushPendingApply();

        Assert.Equal(2, view.ApplyCount); // one for the initial bind, one for the whole burst
        // Content accumulates (real AppendOutput usage) rather than being replaced each time — the
        // burst's very first and very last lines both being present confirms nothing in the middle
        // was skipped by the coalescing, only that the intermediate APPLIES were coalesced.
        Assert.Contains("<body><div>0</div>", view.RenderedHtml);
        Assert.Contains("<div>999</div></body>", view.RenderedHtml);
    }

    // Live bug report: "переодически пропадает вывод в results если в процессе работы скрипта
    // переключать вкладки" (output periodically disappears if you switch tabs while a script is
    // running). DataContext (State.Output) is set exactly once per tab and never reassigned
    // (EditorTabItemControl.cs:75) — but switching away from a tab (either the outer file tabs, or
    // this control's own sibling Results/Debugger tabs, EditorTabItemControl.axaml) detaches this
    // control from the visual tree without touching DataContext at all. Before this fix, only
    // DataContextChanged triggered a (re)subscribe, so a script that kept running on a tab the user
    // had switched away from wrote into a model nobody was listening to anymore — reattaching never
    // resynced, freezing the panel stale from the first switch onward.
    [AvaloniaFact]
    public async Task Should_ResumeShowingNewOutput_AfterBeingDetachedAndReattachedFromTheVisualTree()
    {
        var view = new OutputHtmlView();
        var vm = new OutputHtmlViewModel();
        vm.Append("<div>before</div>");
        var host = new ContentControl { Content = view };
        var window = new Window { Content = host };
        window.Show();

        view.DataContext = vm;
        await FlushPendingApply();
        Assert.Contains("before", view.RenderedHtml);

        // Simulate switching tabs away: the ContentPresenter that was showing this control now
        // shows something else, detaching it from the visual tree — the script keeps running
        // regardless, appending more output while nothing is visually bound.
        host.Content = null;
        await FlushPendingApply();
        vm.Append("<div>while-detached</div>");

        // Switch back.
        host.Content = view;
        await FlushPendingApply();

        Assert.Contains("while-detached", view.RenderedHtml);
    }

    // The real apply runs at Background dispatcher priority — awaiting a no-op posted at the same
    // priority guarantees anything queued ahead of it (i.e. the pending apply) has already run,
    // without depending on a specific "flush the queue" API.
    private static async Task FlushPendingApply()
        => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
