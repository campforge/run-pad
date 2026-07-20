using System.Text;
using System.Text.Json;
using Xunit;

namespace DotPad.Runtime.Tests;

// TODO.md #63 "интерактивный типизированный ввод" — Util.ReadLine/GetPassword/ReadLine<T>. Same
// Console.SetOut/SetIn redirection technique as ObjectExtensionsTests, since this is the runtime-side
// half of the same sentinel-prefixed stdout protocol Dump uses (a different prefix char, see
// Util.InputRequestPrefixChar) paired with a plain-text stdin reply. [Collection(...)] — see
// ConsoleTestCollection: Console.Out/In are process-wide, so this can't run concurrently with
// ObjectExtensionsTests either.
[Collection(ConsoleTestCollection.Name)]
public class UtilTests
{
    private static string DecodeRequest(string stdout)
    {
        var line = stdout.TrimEnd('\r', '\n');
        Assert.StartsWith(Util.InputRequestPrefixChar.ToString(), line);
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(line[1..]));
        return json;
    }

    [Fact]
    public void ReadLine_WritesSentinelPrefixedRequest_ThenReturnsTheStdinAnswer()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            Console.SetIn(new StringReader("Alice\n"));

            var result = Util.ReadLine("Name?");

            Assert.Equal("Alice", result);
            var json = DecodeRequest(sw.ToString());
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("Name?", doc.RootElement.GetProperty("Prompt").GetString());
            Assert.False(doc.RootElement.GetProperty("IsPassword").GetBoolean());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void GetPassword_MarksTheRequestAsPassword()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            Console.SetIn(new StringReader("hunter2\n"));

            var result = Util.GetPassword("Password?");

            Assert.Equal("hunter2", result);
            var json = DecodeRequest(sw.ToString());
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("IsPassword").GetBoolean());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void ReadLine_Should_ReturnNull_WhenStdinIsClosed()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetIn(new StringReader("")); // ReadLine() on an exhausted reader returns null (EOF)

            var result = Util.ReadLine();

            Assert.Null(result);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void ReadLineOfT_Should_ConvertTheAnswer_ToTheRequestedType()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetIn(new StringReader("42\n"));

            var result = Util.ReadLine<int>("Age?");

            Assert.Equal(42, result);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }

    [Fact]
    public void ReadLineOfT_Should_ReturnDefault_WhenStdinIsClosed()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        try
        {
            Console.SetOut(new StringWriter());
            Console.SetIn(new StringReader(""));

            var result = Util.ReadLine<int>();

            Assert.Equal(0, result);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }
}
