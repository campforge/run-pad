using System.Text;
using Xunit;

namespace DotPad.Runtime.Tests;

public class ObjectExtensionsTests
{
    [Fact]
    public void Dump_ReturnsOriginalObject()
    {
        // Arrange
        var obj = new object();

        // Act
        var result = obj.Dump();

        // Assert
        Assert.Same(obj, result);
    }

    [Fact]
    public void Dump_ValueType_ReturnsOriginalValue()
    {
        // Act
        var result = 42.Dump();

        // Assert
        Assert.Equal(42, result);
    }

    [Fact]
    public void Dump_WritesLineStartingWithPrefixChar()
    {
        // Arrange
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            // Act
            "test".Dump();

            var output = sw.ToString();

            // Assert
            Assert.StartsWith(ObjectExtensions.DumpLinePrefixChar.ToString(), output);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Dump_OutputIsBase64EncodedHtml()
    {
        // Arrange
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            // Act
            42.Dump("my label");

            var decoded = DecodeDump(sw.ToString());

            // Assert
            Assert.Contains("dp-label", decoded);
            Assert.Contains("dp-number", decoded);
            Assert.Contains("42", decoded);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Dump_EnablesMethodChaining()
    {
        // Arrange
        var originalOut = Console.Out;
        try
        {
            Console.SetOut(new StringWriter());

            // Act — chaining two Dump calls
            var result = "hello".Dump().Dump("second");

            // Assert
            Assert.Equal("hello", result);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // LINQPad compatibility: x.Dump(3) sets depth positionally with a plain int, which the main
    // Dump(this T o, string? label = null, ...) overload can't accept directly (an int argument
    // can't bind to that overload's leading string? label parameter).
    [Fact]
    public void Dump_Int_ReturnsOriginalObject_LikeLinqPad()
    {
        var result = 42.Dump(1);

        Assert.Equal(42, result);
    }

    [Fact]
    public void Dump_Int_SetsMaxDepth_LikeLinqPadsDumpDepthOverload()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var obj = new Nested { Value = 1, Inner = new Nested { Value = 2 } };

            // Act — LINQPad-shaped positional depth overload: truncate immediately (depth 0).
            obj.Dump(0);

            var decoded = DecodeDump(sw.ToString());

            // Assert
            Assert.Contains("dp-truncated", decoded);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Dump_Int_LabelsWithSourceExpression_WhenNoDescriptionGiven()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var myValue = 42;

            // Act
            myValue.Dump(1);

            var decoded = DecodeDump(sw.ToString());

            // Assert — same auto-label-from-expression behavior as the no-arg Dump()
            Assert.Contains("dp-label", decoded);
            Assert.Contains("myValue", decoded);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void Dump_StringAndInt_SetsLabelAndMaxDepth_LikeLinqPad()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var obj = new Nested { Value = 1, Inner = new Nested { Value = 2 } };

            // Act — LINQPad's Dump(string description, int depth) shape, via named/positional args
            // on the main overload (no dedicated overload needed — see ObjectExtensions.cs).
            obj.Dump("my label", 0);

            var decoded = DecodeDump(sw.ToString());

            // Assert
            Assert.Contains("dp-label", decoded);
            Assert.Contains("my label", decoded);
            Assert.Contains("dp-truncated", decoded);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    private static string DecodeDump(string consoleOutput)
    {
        var line = consoleOutput.Trim();
        var base64 = line.TrimStart(ObjectExtensions.DumpLinePrefixChar);
        return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
    }

    private sealed class Nested
    {
        public int Value { get; set; }
        public Nested? Inner { get; set; }
    }
}
