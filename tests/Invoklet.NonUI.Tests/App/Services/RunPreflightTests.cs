using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Invoklet.Services;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class RunPreflightTests
{
    [Fact]
    public async Task CheckAsync_SavedScriptWithValidArguments_MayProceedAfterTheChecksInOrder()
    {
        // Arrange
        var fixture = new RunPreflightFixture();
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.CheckAsync(RunPreflightFixture.CreateInput(), fixture.Save, fixture.Abort, fixture.Arguments);

        // Assert
        Assert.IsType<RunMayProceed>(outcome);
        Assert.Equal(["save", "abort?", "arguments"], fixture.Calls);
    }

    [Fact]
    public async Task CheckAsync_InvalidDocument_IsBlockedBeforeAnythingIsSaved()
    {
        // Arrange
        var fixture = new RunPreflightFixture();
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.CheckAsync(RunPreflightFixture.CreateInput(parseError: "bad front matter"), fixture.Save, fixture.Abort, fixture.Arguments);

        // Assert
        Assert.Equal("bad front matter", Assert.IsType<RunBlockedByInvalidDocument>(outcome).ParseError);
        Assert.Empty(fixture.Calls);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("/scripts/a.inv", true)]
    public async Task CheckAsync_UntitledScriptOrSelection_IsNotSaved(string? filePath, bool isSelection)
    {
        // Arrange
        var fixture = new RunPreflightFixture();
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.CheckAsync(RunPreflightFixture.CreateInput(filePath: filePath, isSelection: isSelection), fixture.Save, fixture.Abort, fixture.Arguments);

        // Assert
        Assert.IsType<RunMayProceed>(outcome);
        Assert.Equal(["arguments"], fixture.Calls);
    }

    [Fact]
    public async Task CheckAsync_SaveFails_IsBlockedWithTheFileNameAndTheReason()
    {
        // Arrange
        var fixture = new RunPreflightFixture { SaveResult = new InPlaceSaveResult(InPlaceSaveStatus.Failed, "disk full") };
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.CheckAsync(RunPreflightFixture.CreateInput(), fixture.Save, fixture.Abort, fixture.Arguments);

        // Assert
        var blocked = Assert.IsType<RunBlockedBySaveFailure>(outcome);
        Assert.Equal("a.inv", blocked.FileName);
        Assert.Equal("disk full", blocked.Error);
        Assert.DoesNotContain("arguments", fixture.Calls);
    }

    [Fact]
    public async Task CheckAsync_SaveCancelled_AbortsWithoutAMessage()
    {
        // Arrange
        var fixture = new RunPreflightFixture { SaveResult = InPlaceSaveResult.Cancelled };
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.CheckAsync(RunPreflightFixture.CreateInput(), fixture.Save, fixture.Abort, fixture.Arguments);

        // Assert
        Assert.IsType<RunAborted>(outcome);
        Assert.DoesNotContain("arguments", fixture.Calls);
    }

    [Fact]
    public async Task CheckAsync_StoppedWhileSaving_AbortsEvenIfTheSaveFailed()
    {
        // Arrange
        var fixture = new RunPreflightFixture { MustAbort = true, SaveResult = new InPlaceSaveResult(InPlaceSaveStatus.Failed, "late") };
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.CheckAsync(RunPreflightFixture.CreateInput(), fixture.Save, fixture.Abort, fixture.Arguments);

        // Assert
        Assert.IsType<RunAborted>(outcome);
    }

    [Fact]
    public async Task CheckAsync_ArgumentSetIsGone_IsBlockedWithTheMessage()
    {
        // Arrange
        var fixture = new RunPreflightFixture { ArgumentsError = "argument set 'prod' not found" };
        var sut = fixture.CreateSut();

        // Act
        var outcome = await sut.CheckAsync(RunPreflightFixture.CreateInput(), fixture.Save, fixture.Abort, fixture.Arguments);

        // Assert
        Assert.Equal("argument set 'prod' not found", Assert.IsType<RunBlockedByArguments>(outcome).Message);
    }
}
