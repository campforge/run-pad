using System.Text;
using Xunit;

namespace RunPad.Runtime.Tests;

// LINQPad-inspired Util.ProgressBar. Redirects Console.Out — see
// ConsoleTestCollection for why this needs to run sequentially relative to every other test doing the
// same (UtilTests/ObjectExtensionsTests).
[Collection(ConsoleTestCollection.Name)]
public class UtilProgressBarTests
{
    private static string[] CapturedLines(StringWriter sw)
        => sw.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    private static string DecodeDump(string line)
    {
        var base64 = line.TrimStart(ObjectExtensions.DumpLinePrefixChar);
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    [Fact]
    public void Constructing_Should_NotEmitAnything_BeforeDumpIsCalled()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            _ = new Util.ProgressBar(42, "Loading");

            Assert.Empty(CapturedLines(sw));
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void Dump_Should_EmitOneLine_WithThePercentAndCaption()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            new Util.ProgressBar(42, "Loading").Dump();

            var lines = CapturedLines(sw);
            Assert.Single(lines);
            var decoded = DecodeDump(lines[0]);
            Assert.Contains("dp-progressbar", decoded);
            Assert.Contains("data-dp-percent=\"42\"", decoded);
            Assert.Contains("width:42%", decoded);
            Assert.Contains("Loading", decoded);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void Dump_Should_ReturnTheSameInstance_ForChaining()
    {
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());
            var pb = new Util.ProgressBar();

            var result = pb.Dump();

            Assert.Same(pb, result);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void PercentComplete_Setter_Should_ReRenderInPlace_WithTheSameId()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            var pb = new Util.ProgressBar(0);
            pb.Dump();

            pb.PercentComplete = 80;

            var lines = CapturedLines(sw);
            Assert.Equal(2, lines.Length);
            var firstId = ExtractId(DecodeDump(lines[0]));
            var secondDecoded = DecodeDump(lines[1]);
            Assert.Equal(firstId, ExtractId(secondDecoded));
            Assert.Contains("data-dp-percent=\"80\"", secondDecoded);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void PercentComplete_Setter_Should_BeANoOp_BeforeDumpIsCalled()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            var pb = new Util.ProgressBar(0);

            pb.PercentComplete = 50;

            Assert.Empty(CapturedLines(sw));
        }
        finally { Console.SetOut(originalOut); }
    }

    [Theory]
    [InlineData(-10, 0)]
    [InlineData(150, 100)]
    [InlineData(55, 55)]
    public void PercentComplete_Should_ClampToTheValidRange(int input, int expected)
    {
        var pb = new Util.ProgressBar(input);

        Assert.Equal(expected, pb.PercentComplete);
    }

    [Fact]
    public void Caption_Should_BeHtmlEncoded()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            new Util.ProgressBar(0, "<script>A & B</script>").Dump();

            var decoded = DecodeDump(CapturedLines(sw)[0]);
            Assert.DoesNotContain("<script>", decoded);
            Assert.Contains("&lt;script&gt;", decoded);
            Assert.Contains("A &amp; B", decoded);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void Refresh_Should_ReEmit_WithTheCurrentState()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            var pb = new Util.ProgressBar(10);
            pb.Dump();

            pb.Refresh();

            Assert.Equal(2, CapturedLines(sw).Length);
        }
        finally { Console.SetOut(originalOut); }
    }

    private static string ExtractId(string decodedHtml)
    {
        const string marker = "data-dp-id=\"";
        var start = decodedHtml.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = decodedHtml.IndexOf('"', start);
        return decodedHtml.Substring(start, end - start);
    }
}
