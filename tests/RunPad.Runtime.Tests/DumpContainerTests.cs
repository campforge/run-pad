using System.Text;
using Xunit;

namespace RunPad.Runtime.Tests;

// LINQPad Runtime API compatibility — DumpContainer's real shape
// (UpdateContent/AppendContent/ClearContent, Style, DumpDepth, the Rx-style When*Changed observables)
// verified against the real LINQPad.Runtime.dll. Deliberately no Dump() method of its own — see the
// class's own doc comment for why an instance method would shadow every ObjectExtensions.Dump
// overload for this type — so these tests go through the real extension methods, same as any script.
[Collection(ConsoleTestCollection.Name)]
public class DumpContainerTests
{
    private static string DecodeLastLine(StringWriter sw)
    {
        var line = sw.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1].Trim();
        var base64 = line.TrimStart(ObjectExtensions.DumpLinePrefixChar);
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    [Fact]
    public void Dump_ThenUpdateContent_ReRendersWithTheSameStableId()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var dc = new DumpContainer("v1");
            dc.Dump(); // via the extension method — DumpContainer has no Dump() of its own
            var firstId = ExtractDpId(DecodeLastLine(sw));

            dc.UpdateContent("v2");
            var secondId = ExtractDpId(DecodeLastLine(sw));

            Assert.Equal(firstId, secondId);
            Assert.Contains("v2", DecodeLastLine(sw));
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void Content_Setter_IsEquivalentToUpdateContent()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var dc = new DumpContainer();
            dc.Dump();
            dc.Content = "set via property";

            Assert.Contains("set via property", DecodeLastLine(sw));
            Assert.Equal("set via property", dc.Content);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void UpdateContent_BeforeDump_DoesNotWriteAnything()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var dc = new DumpContainer();
            dc.UpdateContent("never shown"); // not dumped yet — must be a no-op

            Assert.Equal("", sw.ToString());
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void AppendContent_AddsToExistingContent_RatherThanReplacingIt()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var dc = new DumpContainer("first");
            dc.Dump();
            dc.AppendContent("second");

            var decoded = DecodeLastLine(sw);
            Assert.Contains("first", decoded);
            Assert.Contains("second", decoded);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void ClearContent_RemovesBothSetAndAppendedContent()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var dc = new DumpContainer("first");
            dc.Dump();
            dc.AppendContent("second");
            dc.ClearContent();

            var decoded = DecodeLastLine(sw);
            Assert.DoesNotContain("first", decoded);
            Assert.DoesNotContain("second", decoded);
            Assert.Null(dc.Content);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void Style_Setter_AppliesToTheWrapperElement_AndReRenders()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var dc = new DumpContainer("x");
            dc.Dump();
            dc.Style = "color:red";

            Assert.Contains("style=\"color:red\"", DecodeLastLine(sw));
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void ContentChanged_And_WhenContentChanged_BothFire_OnUpdateContent()
    {
        var dc = new DumpContainer();
        var eventFired = false;
        dc.ContentChanged += (_, _) => eventFired = true;
        object? observed = "not set";
        using var subscription = dc.WhenContentChanged().Subscribe(new AnonymousObserver<object?>(v => observed = v));

        dc.UpdateContent("new value");

        Assert.True(eventFired);
        Assert.Equal("new value", observed);
    }

    [Fact]
    public void ContentAppended_And_WhenContentAppended_BothFire_OnAppendContent()
    {
        var dc = new DumpContainer();
        object? eventValue = null;
        dc.ContentAppended += v => eventValue = v;
        object? observed = null;
        using var subscription = dc.WhenContentAppended().Subscribe(new AnonymousObserver<object?>(v => observed = v));

        dc.AppendContent("appended");

        Assert.Equal("appended", eventValue);
        Assert.Equal("appended", observed);
    }

    [Fact]
    public void StyleChanged_And_WhenStyleChanged_BothFire_OnStyleSet()
    {
        var dc = new DumpContainer();
        var eventFired = false;
        dc.StyleChanged += (_, _) => eventFired = true;
        string? observed = null;
        using var subscription = dc.WhenStyleChanged().Subscribe(new AnonymousObserver<string?>(v => observed = v));

        dc.Style = "color:blue";

        Assert.True(eventFired);
        Assert.Equal("color:blue", observed);
    }

    [Fact]
    public void Subscription_Dispose_StopsFurtherNotifications()
    {
        var dc = new DumpContainer();
        var callCount = 0;
        var subscription = dc.WhenContentChanged().Subscribe(new AnonymousObserver<object?>(_ => callCount++));

        dc.UpdateContent("first");
        subscription.Dispose();
        dc.UpdateContent("second");

        Assert.Equal(1, callCount);
    }

    [Fact]
    public void DumpDepth_LimitsExpansionOfThisContainersOwnContent()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var dc = new DumpContainer(new { Nested = new { Value = 1 } }) { DumpDepth = 0 };
            dc.Dump();

            Assert.Contains("dp-truncated", DecodeLastLine(sw));
        }
        finally { Console.SetOut(originalOut); }
    }

    private static string ExtractDpId(string html)
    {
        const string marker = "data-dp-id=\"";
        var start = html.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = html.IndexOf('"', start);
        return html[start..end];
    }

    private sealed class AnonymousObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnNext(T value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }
}
