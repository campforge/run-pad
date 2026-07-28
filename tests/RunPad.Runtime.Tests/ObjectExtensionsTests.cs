using System.Text;
using RunPad.Runtime;
using Xunit;

namespace RunPad.Runtime.Tests;

// Redirects Console.Out — see ConsoleTestCollection for why this needs to run sequentially relative
// to every other test doing the same (UtilTests).
[Collection(ConsoleTestCollection.Name)]
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
    public void Dump_NoArgs_LabelsWithSourceExpression()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var myValue = 42;

            // Act — the only shape that auto-labels from the dumped expression.
            myValue.Dump();

            var decoded = DecodeDump(sw.ToString());

            // Assert
            Assert.Contains("dp-label", decoded);
            Assert.Contains("myValue", decoded);
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

    // LINQPad compatibility: x.Dump(3) sets depth positionally with a plain int — its own overload
    // (Dump<T>(this T o, int depth)) since an int argument can't bind to the label/expression
    // overload's leading string? parameter.
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
    public void Dump_Int_DoesNotAutoLabel_UnlikeTheNoArgOverload()
    {
        // Auto-labeling from the source expression is only needed for the empty Dump() —
        // Dump(depth) has no label parameter to fall back from at all.
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var myValue = 42;

            // Act
            myValue.Dump(1);

            var decoded = DecodeDump(sw.ToString());

            // Assert
            Assert.DoesNotContain("dp-label", decoded);
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

            // Act — LINQPad's Dump(string description, int depth) shape.
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

    [Fact]
    public void Dump_Options_AppliesEveryRunPadSpecificRenderingOption()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var obj = new Nested { Value = 1, Inner = new Nested { Value = 2 } };

            // Act
            obj.Dump(new DumpOptions { Description = "advanced", MaxDepth = 0 });

            var decoded = DecodeDump(sw.ToString());

            // Assert
            Assert.Contains("dp-label", decoded);
            Assert.Contains("advanced", decoded);
            Assert.Contains("dp-truncated", decoded);
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    // LINQPad compatibility (2026-07-28): Dump(int depth, int? collapseTo) — RunPad originally shipped
    // this overload without the second parameter at all, meaning a real LINQPad script calling
    // x.Dump(3, 1) wouldn't have compiled.
    [Fact]
    public void Dump_IntInt_SetsBothDepthAndCollapseTo_LikeLinqPad()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            var obj = new Nested { Value = 1, Inner = new Nested { Value = 2 } };

            obj.Dump(0, 0);

            var decoded = DecodeDump(sw.ToString());
            Assert.Contains("dp-truncated", decoded); // depth 0 truncates immediately regardless of collapseTo
        }
        finally { Console.SetOut(originalOut); }
    }

    // LINQPad compatibility (2026-07-28): Dump(bool toDataGrid, string? description = null) and its
    // argument-order sibling Dump(string? description, bool toDataGrid) — real LINQPad exposes both.
    [Fact]
    public void Dump_BoolDescription_ReturnsOriginalObject_LikeLinqPad()
    {
        var result = 42.Dump(true, "grid");

        Assert.Equal(42, result);
    }

    [Fact]
    public void Dump_BoolDescription_SetsTheLabel()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            42.Dump(true, "grid label");

            var decoded = DecodeDump(sw.ToString());
            Assert.Contains("dp-label", decoded);
            Assert.Contains("grid label", decoded);
        }
        finally { Console.SetOut(originalOut); }
    }

    [Fact]
    public void Dump_DescriptionBool_SetsTheLabel_SameAsTheOtherArgumentOrder()
    {
        var originalOut = Console.Out;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);

            42.Dump("grid label 2", true);

            var decoded = DecodeDump(sw.ToString());
            Assert.Contains("dp-label", decoded);
            Assert.Contains("grid label 2", decoded);
        }
        finally { Console.SetOut(originalOut); }
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
