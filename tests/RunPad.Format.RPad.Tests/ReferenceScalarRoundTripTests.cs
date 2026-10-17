using System.Text.Json;
using Xunit;

namespace RunPad.Format.RPad.Tests;

public sealed class ReferenceScalarRoundTripTests
{
    [Theory]
    [InlineData("[lib]/日本語.dll")]
    [InlineData("- helper.dll")]
    [InlineData("? helper.dll")]
    [InlineData(" leading.dll ")]
    [InlineData("]lib.dll")]
    [InlineData("}lib.dll")]
    [InlineData(",lib.dll")]
    [InlineData("null")]
    [InlineData("~")]
    [InlineData("dir/it's.dll")]
    [InlineData("C:\\lib\\test.dll")]
    [InlineData("日本語/🌍.dll")]
    [InlineData("tab\tname.dll")]
    [InlineData("line\nname.dll")]
    [InlineData("line\r\nname.dll")]
    [InlineData("line\u0085name.dll")]
    [InlineData("line\u2028name.dll")]
    [InlineData("control\u0001name.dll")]
    public void SavingAReference_PreservesItsExactString_AndTheMarkdown(string reference)
    {
        const string body = "\r\n# 日本語 🌍\r\nBefore\r\n```csharp run\r\n1.Dump();\r\n```\r\nAfter\r\n";
        var source = "---\nlanguage: csharp\nreferences:\n  - " + JsonSerializer.Serialize(reference) + "\n---\n" + body;
        var parsed = RpadParser.Parse(source);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal(reference, Assert.Single(parsed.Document.FrontMatter.References!));

        for (var save = 0; save < 2; save++)
        {
            var written = RpadWriter.Write(parsed.Document);
            Assert.EndsWith(body, written, StringComparison.Ordinal);
            parsed = RpadParser.Parse(written);
            Assert.True(parsed.IsSuccess, parsed.Error);
            Assert.Equal(reference, Assert.Single(parsed.Document.FrontMatter.References!));
        }
    }
}
