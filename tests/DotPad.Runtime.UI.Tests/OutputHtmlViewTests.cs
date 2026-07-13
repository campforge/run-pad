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
        var vm = new OutputHtmlViewModel { Css = "body { color: red; }", Content = "<div>hi</div>" };

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
            vm.Content = $"<div>{i}</div>";
        await FlushPendingApply();

        Assert.Equal(2, view.ApplyCount); // one for the initial bind, one for the whole burst
        Assert.Contains("<body><div>999</div></body>", view.RenderedHtml);
    }

    // The real apply runs at Background dispatcher priority — awaiting a no-op posted at the same
    // priority guarantees anything queued ahead of it (i.e. the pending apply) has already run,
    // without depending on a specific "flush the queue" API.
    private static async Task FlushPendingApply()
        => await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
}
