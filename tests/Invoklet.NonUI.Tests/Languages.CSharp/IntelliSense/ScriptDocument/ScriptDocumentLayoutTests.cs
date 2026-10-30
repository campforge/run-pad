using Invoklet.Format.MdRun;
using Invoklet.Languages.CSharp.IntelliSense.ScriptDocument;
using Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.ScriptDocument.Fixtures;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Invoklet.NonUI.Tests.Languages.CSharp.IntelliSense.ScriptDocument;

public sealed class ScriptDocumentLayoutTests
{
    [Fact]
    public void Preamble_New_HasTheImplicitUsingsOfTheCompilerAndTheRuntimeAndEndsWithABlankLine()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();

        // Act
        var preamble = sut.Preamble;

        // Assert
        Assert.StartsWith("global using System;\n", preamble);
        Assert.Contains("global using Invoklet.Runtime;\n", preamble);
        Assert.EndsWith(";\n\n", preamble);
    }

    [Fact]
    public void SetExtraUsings_AUsingTheBaseDoesNotHave_AddsItToThePreamble()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();

        // Act
        var changed = sut.SetExtraUsings(["Newtonsoft.Json"]);

        // Assert
        Assert.True(changed);
        Assert.Contains("global using Newtonsoft.Json;\n", sut.Preamble);
    }

    [Fact]
    public void SetExtraUsings_OnlyBaseUsingsOrTheSameAgain_ChangesNothing()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        sut.SetExtraUsings(["Newtonsoft.Json"]);
        var before = sut.Preamble;

        // Act
        var sameAgain = sut.SetExtraUsings(["Newtonsoft.Json", "Newtonsoft.Json"]);
        var baseOnly = new ScriptDocumentLayoutFixture().CreateSut().SetExtraUsings(["System", "Invoklet.Runtime"]);

        // Assert
        Assert.False(sameAgain);
        Assert.False(baseOnly);
        Assert.Equal(before, sut.Preamble);
    }

    [Fact]
    public void SetExtraUsings_AUsingRemoved_TakesItOutOfThePreamble()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        sut.SetExtraUsings(["Newtonsoft.Json"]);

        // Act
        var changed = sut.SetExtraUsings([]);

        // Assert
        Assert.True(changed);
        Assert.DoesNotContain("Newtonsoft", sut.Preamble);
    }

    [Fact]
    public void SetKind_ANewKind_IsTrueOnceAndFalseForTheSameAgain()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();

        // Act
        var first = sut.SetKind(MdRunKind.Program);
        var again = sut.SetKind(MdRunKind.Program);

        // Assert
        Assert.True(first);
        Assert.False(again);
        Assert.Equal(MdRunKind.Program, sut.Kind);
    }

    [Fact]
    public void Statements_Code_HasNoWrapperAndTheDocumentIsPreambleCodeAndThePad()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        const string code = "var a = 1;";

        // Act
        var fullText = sut.FullText(code);

        // Assert
        Assert.Equal("", sut.Prefix(code));
        Assert.Equal("", sut.Suffix(code));
        Assert.Equal(sut.Preamble + code + ScriptDocumentLayout.TrailingPad, fullText);
        Assert.Equal(sut.Preamble.Length, sut.HeaderLength(code));
    }

    [Fact]
    public void ToDocumentOffset_AndBack_AreInverseOfEachOtherForStatements()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        const string code = "var a = 1;";

        // Act
        var inDocument = sut.ToDocumentOffset(code, 4);
        var backInCode = sut.ToUserOffset(code, inDocument);

        // Assert
        Assert.Equal(sut.Preamble.Length + 4, inDocument);
        Assert.Equal(4, backInCode);
    }

    [Fact]
    public void ToUserOffset_OffsetInThePreamble_IsMinusOne()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();

        // Act
        var offset = sut.ToUserOffset("var a = 1;", 3);

        // Assert
        Assert.Equal(-1, offset);
    }

    [Fact]
    public void ToUserSpan_SpanThatStartsInThePreamble_IsNullAndOneInTheCodeIsMapped()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        const string code = "var a = 1;";

        // Act
        var inPreamble = sut.ToUserSpan(code, new TextSpan(2, 5));
        var inCode = sut.ToUserSpan(code, new TextSpan(sut.Preamble.Length + 4, 1));

        // Assert
        Assert.Null(inPreamble);
        Assert.Equal(new TextSpan(4, 1), inCode);
    }

    [Fact]
    public void Program_Code_IsWrappedInAClassWhoseOpeningIsInTheHeaderAndWhoseClosingIsTheSuffix()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        sut.SetKind(MdRunKind.Program);
        var code = ScriptDocumentLayoutFixture.ProgramCode;

        // Act
        var fullText = sut.FullText(code);

        // Assert
        Assert.NotEqual("", sut.Prefix(code));
        Assert.NotEqual("", sut.Suffix(code));
        Assert.Equal(sut.Preamble.Length + sut.Prefix(code).Length, sut.HeaderLength(code));
        Assert.StartsWith(sut.Preamble + sut.Prefix(code), fullText);
        Assert.EndsWith(sut.Suffix(code) + ScriptDocumentLayout.TrailingPad, fullText);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(14)]
    public void Program_OffsetsOfTheCode_SurviveTheRoundTripThroughTheDocument(int offset)
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        sut.SetKind(MdRunKind.Program);
        var code = ScriptDocumentLayoutFixture.ProgramCode;

        // Act
        var back = sut.ToUserOffset(code, sut.ToDocumentOffset(code, offset));

        // Assert
        Assert.Equal(offset, back);
    }

    [Fact]
    public void ParseOptions_Set_IsWhatIsReadBack()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        var options = sut.ParseOptions.WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.CSharp9);

        // Act
        sut.ParseOptions = options;

        // Assert
        Assert.Same(options, sut.ParseOptions);
    }

    [Fact]
    public async Task FullText_WhileTheUsingsChange_IsAlwaysBuiltFromOnePreamble()
    {
        // Arrange
        var sut = new ScriptDocumentLayoutFixture().CreateSut();
        sut.SetKind(MdRunKind.Program);
        var code = ScriptDocumentLayoutFixture.ProgramCode;
        using var stop = new CancellationTokenSource();
        var changer = Task.Run(() =>
        {
            for (var i = 0; !stop.IsCancellationRequested; i++) sut.SetExtraUsings(i % 2 == 0 ? ["Newtonsoft.Json"] : []);
        }, TestContext.Current.CancellationToken);

        // Act
        var mismatches = 0;
        for (var i = 0; i < 2000; i++)
        {
            var text = sut.FullText(code);
            var header = text.IndexOf("void Main", StringComparison.Ordinal);
            // The code starts right after the header: a text built from two preambles would put it elsewhere.
            if (!text.Contains("\n\n") || header < 0 || text.IndexOf("global using", header, StringComparison.Ordinal) >= 0) mismatches++;
        }

        await stop.CancelAsync();
        await changer;

        // Assert
        Assert.Equal(0, mismatches);
    }
}
