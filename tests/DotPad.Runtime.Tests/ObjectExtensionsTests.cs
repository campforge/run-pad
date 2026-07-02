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

            var line = sw.ToString().Trim();
            var base64 = line.TrimStart(ObjectExtensions.DumpLinePrefixChar);
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64));

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
}
