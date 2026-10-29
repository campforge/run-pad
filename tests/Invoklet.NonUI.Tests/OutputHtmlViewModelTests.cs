using System.ComponentModel;
using Invoklet.UserControls.Models;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

// EditorTabItemControl.AppendOutput used to do `Output.Content += line` once per
// output line — each `+=` copies the ENTIRE accumulated string so far, making total cost O(n²) for
// n lines. Content is now read-only, backed by a StringBuilder mutated through Append/Replace/Clear.
public class OutputHtmlViewModelTests
{
    [Fact]
    public void Append_Should_AccumulateInOrder()
    {
        var vm = new OutputHtmlViewModel();

        vm.Append("<div>a</div>");
        vm.Append("<div>b</div>");
        vm.Append("<div>c</div>");

        Assert.Equal("<div>a</div><div>b</div><div>c</div>", vm.Content);
    }

    [Fact]
    public void Replace_Should_SwapAPreviouslyAppendedBlock_InPlace()
    {
        // Mirrors EditorTabItemControl.RenderDump's exact usage: a DumpContainer re-emits the same
        // data-dp-id on every update, and the previous block for that id gets replaced in place
        // instead of a new one being appended after it.
        var vm = new OutputHtmlViewModel();
        vm.Append("<div>before</div>");
        vm.Append("<div data-dp-id=\"1\">old</div>");
        vm.Append("<div>after</div>");

        vm.Replace("<div data-dp-id=\"1\">old</div>", "<div data-dp-id=\"1\">new</div>");

        Assert.Equal("<div>before</div><div data-dp-id=\"1\">new</div><div>after</div>", vm.Content);
    }

    [Fact]
    public void Clear_Should_EmptyTheContent()
    {
        var vm = new OutputHtmlViewModel();
        vm.Append("<div>a</div>");

        vm.Clear();

        Assert.Equal("", vm.Content);
    }

    [Fact]
    public void Append_Should_RaisePropertyChanged_ForContent_WithoutEvaluatingItsGetter()
    {
        // The whole point of Content being read-only/StringBuilder-backed: OutputHtmlView subscribes
        // to the raw PropertyChanged event and must NOT need to touch vm.Content itself to learn that
        // something changed — evaluating the getter (ToString()) on every single Append would put the
        // O(n²) cost right back, just moved to the subscriber instead of the model.
        var vm = new OutputHtmlViewModel();
        var raisedFor = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => raisedFor.Add(e.PropertyName);

        vm.Append("<div>a</div>");

        Assert.Equal([nameof(OutputHtmlViewModel.Content)], raisedFor);
    }

    // Correctness at a realistic scale (a script dumping thousands of output lines), not a
    // performance assertion — StringBuilder.Append's amortized-O(1) behavior is a documented BCL
    // guarantee, not something worth re-proving with a flaky wall-clock timing test here. This just
    // guards against an off-by-one/truncation bug that only shows up once there's enough content for
    // StringBuilder's internal buffer to actually need to grow a few times.
    [Fact]
    public void Append_Should_AccumulateCorrectly_AtRealisticScale()
    {
        var vm = new OutputHtmlViewModel();
        const int lineCount = 5000;
        const string line = "<div>0123456789</div>"; // fixed-length line, like a real output row

        for (var i = 0; i < lineCount; i++)
            vm.Append(line);

        Assert.Equal(lineCount * line.Length, vm.Content.Length);
        Assert.EndsWith(line, vm.Content);
    }
}
