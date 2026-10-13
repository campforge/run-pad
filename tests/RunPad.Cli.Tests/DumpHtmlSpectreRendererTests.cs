using RunPad.Cli;
using Spectre.Console.Testing;
using Xunit;

namespace RunPad.Cli.Tests;

// These dump
// real DumpRenderer-shaped HTML fragments through DumpHtmlSpectreRenderer into a TestConsole and
// assert on the captured plain output — the exact box-drawing/ANSI bytes aren't pinned (fragile,
// Spectre-version-dependent), just that the right text made it through in the right shape.
public class DumpHtmlSpectreRendererTests
{
    private static string RenderToText(string html)
    {
        var console = new TestConsole();
        console.Write(DumpHtmlSpectreRenderer.Render(html));
        return console.Output;
    }

    [Fact]
    public void Render_Should_ShowLabelAndValue_ForALabeledPrimitive()
    {
        var html = "<div class=\"dp-label\">the answer</div><span class=\"dp-number\">42</span>";

        var output = RenderToText(html);

        Assert.Contains("the answer", output);
        Assert.Contains("42", output);
    }

    [Fact]
    public void Render_Should_ShowNull()
    {
        var output = RenderToText("<span class=\"dp-null\">null</span>");

        Assert.Contains("null", output);
    }

    [Fact]
    public void Render_Should_RenderATableWithHeadersAndRows_ForAnEnumerable()
    {
        var html = "<div class=\"dp-count\">Int32[] &mdash; 3 items</div>"
            + "<table class=\"dp-table\"><thead><tr><th>#</th><th>Value</th></tr></thead>"
            + "<tbody>"
            + "<tr><td class=\"dp-td dp-number\">0</td><td class=\"dp-td\"><span class=\"dp-number\">1</span></td></tr>"
            + "<tr><td class=\"dp-td dp-number\">1</td><td class=\"dp-td\"><span class=\"dp-number\">2</span></td></tr>"
            + "<tr><td class=\"dp-td dp-number\">2</td><td class=\"dp-td\"><span class=\"dp-number\">3</span></td></tr>"
            + "</tbody></table>";

        var output = RenderToText(html);

        Assert.Contains("3 items", output); // &mdash; decoded, not left as a raw XML parse failure
        Assert.Contains("#", output);
        Assert.Contains("Value", output);
        Assert.Contains("1", output);
        Assert.Contains("2", output);
        Assert.Contains("3", output);
    }

    [Fact]
    public void Render_Should_RenderNestedTables_ForAPropertyThatIsItselfACollection()
    {
        // A property grid (RenderObject) whose single property's value is itself an enumerable
        // (RenderEnumerable) — the inner table sits inside the outer table's <td>, same shape
        // DumpRenderer produces for e.g. "new { Tags = new[] { "a", "b" } }.Dump()".
        var html = "<div class=\"dp-type\">Demo</div>"
            + "<table class=\"dp-table\"><thead><tr><th>Property</th><th>Value</th></tr></thead>"
            + "<tbody><tr><td class=\"dp-td dp-propname\">Tags</td><td class=\"dp-td\">"
            + "<table class=\"dp-table\"><thead><tr><th>#</th><th>Value</th></tr></thead>"
            + "<tbody><tr><td class=\"dp-td dp-number\">0</td><td class=\"dp-td\"><span class=\"dp-string\">&quot;a&quot;</span></td></tr></tbody></table>"
            + "</td></tr></tbody></table>";

        var output = RenderToText(html);

        Assert.Contains("Demo", output);
        Assert.Contains("Tags", output);
        Assert.Contains("\"a\"", output);
        // The real signal that this is a *nested table* (Table is itself IRenderable, embedded as a
        // cell's content) rather than the inner table's text having been flattened into the outer
        // cell — flattened text would still contain "a" (XElement.Value concatenates all descendant
        // text regardless of structure), so the substring checks above alone can't tell the two
        // apart. Two separate boxes means two rounded top-left corners.
        Assert.Equal(2, output.Count(c => c == '╭'));
    }

    // Security-adjacent correctness: a dumped string that happens to contain Spectre markup syntax
    // ("[", "]") must be printed literally, not interpreted as a color/style directive — arbitrary
    // user data flows into this renderer unescaped from DumpRenderer's own HTML-encoded text.
    [Fact]
    public void Render_Should_EscapeSpectreMarkupSyntax_InDumpedText()
    {
        var html = "<span class=\"dp-string\">&quot;[red]not actually red[/]&quot;</span>";

        var output = RenderToText(html);

        Assert.Contains("[red]not actually red[/]", output);
    }

    // Util.ProgressBar: its own fixed <div class="dp-progressbar" data-dp-percent="N">
    // wrapping an (always textless) <div class="dp-progressbar-fill">, plus an optional
    // <div class="dp-progressbar-caption">text</div> sibling — see Util.cs's ProgressBar.Emit().
    [Fact]
    public void Render_Should_ShowAFilledBarAndThePercentage_ForAProgressBar()
    {
        var html = "<div class=\"dp-progressbar\" data-dp-percent=\"50\">"
            + "<div class=\"dp-progressbar-fill\" style=\"width:50%\"></div></div>";

        var output = RenderToText(html);

        Assert.Contains("50%", output);
        Assert.Contains('█', output);
        Assert.Contains('░', output);
    }

    [Fact]
    public void Render_Should_ShowTheCaption_AboveAProgressBar()
    {
        var html = "<div class=\"dp-progressbar-caption\">Loading</div>"
            + "<div class=\"dp-progressbar\" data-dp-percent=\"25\">"
            + "<div class=\"dp-progressbar-fill\" style=\"width:25%\"></div></div>";

        var output = RenderToText(html);

        Assert.Contains("Loading", output);
        Assert.Contains("25%", output);
    }

    [Fact]
    public void Render_Should_ShowAnEmptyBar_ForZeroPercent()
    {
        var html = "<div class=\"dp-progressbar\" data-dp-percent=\"0\">"
            + "<div class=\"dp-progressbar-fill\" style=\"width:0%\"></div></div>";

        var output = RenderToText(html);

        Assert.Contains("0%", output);
        Assert.DoesNotContain('█', output);
    }

    [Fact]
    public void Render_Should_ShowAFullBar_ForOneHundredPercent()
    {
        var html = "<div class=\"dp-progressbar\" data-dp-percent=\"100\">"
            + "<div class=\"dp-progressbar-fill\" style=\"width:100%\"></div></div>";

        var output = RenderToText(html);

        Assert.Contains("100%", output);
        Assert.DoesNotContain('░', output);
    }

    // Wrapped inside DumpContainer's own unrecognized-wrapper <div data-dp-id="..."> (the live-update
    // shape both DumpContainer and Util.ProgressBar share) — confirms the default pass-through case
    // and the dp-progressbar case compose correctly, not just in isolation.
    [Fact]
    public void Render_Should_RenderAProgressBar_InsideItsLiveUpdateWrapper()
    {
        var html = "<div data-dp-id=\"abc123\">"
            + "<div class=\"dp-progressbar-caption\">Working</div>"
            + "<div class=\"dp-progressbar\" data-dp-percent=\"70\">"
            + "<div class=\"dp-progressbar-fill\" style=\"width:70%\"></div></div></div>";

        var output = RenderToText(html);

        Assert.Contains("Working", output);
        Assert.Contains("70%", output);
    }
}
