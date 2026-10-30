using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.ScriptDocument;

public sealed class ScriptDocumentProjectionTests
{
    [Fact]
    public void Create_ComposesSyntheticDocumentInOrder()
    {
        var projection = ScriptDocumentProjection.Create("using;", "prefix", "code", "suffix", " ");

        Assert.Equal("using;prefixcodesuffix ", projection.FullText);
    }

    [Theory]
    [InlineData("abc", "abXc")]
    [InlineData("abc", "ab")]
    [InlineData("", "abc")]
    public void ComputeMinimalChange_ReconstructsNewText(string oldText, string newText)
    {
        var change = ScriptDocumentProjection.ComputeMinimalChange(oldText, newText);

        var result = oldText.Remove(change.Span.Start, change.Span.Length)
            .Insert(change.Span.Start, change.NewText ?? "");
        Assert.Equal(newText, result);
    }
}
