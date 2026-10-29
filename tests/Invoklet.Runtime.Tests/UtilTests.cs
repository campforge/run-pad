using System.Text;
using System.Text.Json;
using Xunit;

namespace Invoklet.Runtime.Tests;

// Util.ReadLine/ReadLine<T> and the question Util.GetSecret asks. Same
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

    // A secret the store doesn't have is asked for, masked and named, where Invoklet answers input.
    [Fact]
    public void GetSecret_AsksForAMissingSecret_Masked_AndNamed()
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        var originalFile = Environment.GetEnvironmentVariable(Util.SecretsFileEnvVar);
        var originalInput = Environment.GetEnvironmentVariable(Util.InputEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Util.SecretsFileEnvVar, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "secrets.json"));
            Environment.SetEnvironmentVariable(Util.InputEnvVar, "1");
            var sw = new StringWriter();
            Console.SetOut(sw);
            var name = "test-" + Guid.NewGuid().ToString("N"); // nothing remembered from another test
            Console.SetIn(new StringReader("hunter2\n"));

            var result = Util.GetSecret(name);

            Assert.Equal("hunter2", result);
            var json = DecodeRequest(sw.ToString());
            using var doc = JsonDocument.Parse(json);
            Assert.True(doc.RootElement.GetProperty("IsPassword").GetBoolean());
            Assert.Equal(name, doc.RootElement.GetProperty("Secret").GetString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
            Environment.SetEnvironmentVariable(Util.SecretsFileEnvVar, originalFile);
            Environment.SetEnvironmentVariable(Util.InputEnvVar, originalInput);
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

    // Invoklet.Build.Core's ChildProcess.ForExecution sets this same
    // environment variable (name duplicated, not shared — see Util.CurrentQueryPathEnvVar's own
    // comment) on the real compiled script's child process; here it's just read back. Environment
    // variables are process-wide state, same category of concern as Console.Out/In above — this
    // class is already [Collection(ConsoleTestCollection.Name)], serializing it against any other
    // test in the same collection.
    [Fact]
    public void CurrentQueryPath_Should_ReturnNull_WhenTheEnvironmentVariableIsNotSet()
    {
        var original = Environment.GetEnvironmentVariable(Util.CurrentQueryPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Util.CurrentQueryPathEnvVar, null);

            Assert.Null(Util.CurrentQueryPath);
        }
        finally { Environment.SetEnvironmentVariable(Util.CurrentQueryPathEnvVar, original); }
    }

    [Fact]
    public void CurrentQueryPath_Should_ReturnTheEnvironmentVariablesValue_WhenSet()
    {
        var original = Environment.GetEnvironmentVariable(Util.CurrentQueryPathEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(Util.CurrentQueryPathEnvVar, "/home/user/Documents/Invoklet Queries/a.inv");

            Assert.Equal("/home/user/Documents/Invoklet Queries/a.inv", Util.CurrentQueryPath);
        }
        finally { Environment.SetEnvironmentVariable(Util.CurrentQueryPathEnvVar, original); }
    }

    // The request JSON is written by hand (no System.Text.Json package in Invoklet.Runtime): any prompt
    // text, quotes/backslashes/control characters/non-ASCII included, must reach the host intact.
    [Theory]
    [InlineData("Say \"hi\"\\")]
    [InlineData("line1\nline2\r\n\ttab \u0001\u001f end")]
    [InlineData("Пароль 🔑:")]
    [InlineData(null)]
    public void ReadLine_Should_EncodeAnyPromptAsValidJson(string? prompt)
    {
        var originalOut = Console.Out;
        var originalIn = Console.In;
        try
        {
            var sw = new StringWriter();
            Console.SetOut(sw);
            Console.SetIn(new StringReader("x\n"));

            Util.ReadLine(prompt);

            using var doc = JsonDocument.Parse(DecodeRequest(sw.ToString()));
            var root = doc.RootElement;
            if (prompt is null)
                Assert.Equal(JsonValueKind.Null, root.GetProperty("Prompt").ValueKind);
            else
                Assert.Equal(prompt, root.GetProperty("Prompt").GetString());
            Assert.False(root.GetProperty("IsPassword").GetBoolean());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetIn(originalIn);
        }
    }
}
