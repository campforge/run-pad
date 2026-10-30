using System.Text.Json;
using Invoklet.NonUI.Tests.App.Services.Recovery.Fixtures;
using Invoklet.UserControls.Models;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services.Recovery;

public sealed class SessionSpillStoreTests
{
    [Fact]
    public void TryWrite_SessionNotOpen_ReturnsFalseAndWritesNothing()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        var sut = fixture.CreateSut();

        // Act
        var written = sut.TryWrite(Guid.NewGuid(), fixture.CreateContent());

        // Assert
        Assert.False(written);
        fixture.Files.VerifyNoOtherCalls();
    }

    [Fact]
    public void TryWrite_SessionOpen_WritesTheContentAsJsonIntoTheSpillFolderOfTheSession()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        fixture.OpenSession();
        var tab = Guid.NewGuid();
        byte[]? bytes = null;
        fixture.Files.Setup(f => f.Write(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .Callback((string _, string _, byte[] b) => bytes = b);
        var sut = fixture.CreateSut();

        // Act
        var written = sut.TryWrite(tab, fixture.CreateContent("var a = 1;\nЖ"));

        // Assert
        Assert.True(written);
        fixture.Files.Verify(f => f.Write(SessionSpillStoreFixture.SpillFolder, SessionSpillStoreFixture.FileNameOf(tab), It.IsAny<byte[]>()), Times.Once);
        var stored = JsonSerializer.Deserialize<ReleasedContent>(bytes!)!;
        Assert.Equal("var a = 1;\nЖ", stored.Text);
        Assert.Equal(new EditorViewState(4, 1, 2, 7.5), stored.Editor);
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void TryWrite_FilesCannotBeWritten_ReturnsFalse(Type failure)
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        fixture.OpenSession();
        fixture.Files.Setup(f => f.Write(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>()))
            .Throws((Exception)Activator.CreateInstance(failure)!);
        var sut = fixture.CreateSut();

        // Act
        var written = sut.TryWrite(Guid.NewGuid(), fixture.CreateContent());

        // Assert
        Assert.False(written);
    }

    [Fact]
    public void TryRead_FileHoldsContent_ReturnsIt()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        fixture.OpenSession();
        var tab = Guid.NewGuid();
        var output = new TabOutputState("<div>out</div>", true, new Dictionary<string, string> { ["id"] = "block" });
        fixture.Files.Setup(f => f.Read(SessionSpillStoreFixture.SpillFolder, SessionSpillStoreFixture.FileNameOf(tab)))
            .Returns(fixture.Serialize(fixture.CreateContent("text", output)));
        var sut = fixture.CreateSut();

        // Act
        var read = sut.TryRead(tab);

        // Assert
        Assert.Equal("text", read!.Text);
        Assert.Equal("<div>out</div>", read.Output!.Content);
        Assert.True(read.Output.HadRunError);
        Assert.Equal("block", read.Output.DumpContainerBlocks["id"]);
    }

    [Fact]
    public void TryRead_SessionNotOpen_ReturnsNull()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        var sut = fixture.CreateSut();

        // Act
        var read = sut.TryRead(Guid.NewGuid());

        // Assert
        Assert.Null(read);
        fixture.Files.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(UnauthorizedAccessException))]
    public void TryRead_FileCannotBeRead_ReturnsNull(Type failure)
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        fixture.OpenSession();
        fixture.Files.Setup(f => f.Read(It.IsAny<string>(), It.IsAny<string>())).Throws((Exception)Activator.CreateInstance(failure)!);
        var sut = fixture.CreateSut();

        // Act
        var read = sut.TryRead(Guid.NewGuid());

        // Assert
        Assert.Null(read);
    }

    [Fact]
    public void TryRead_FileIsNotJson_ReturnsNull()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        fixture.OpenSession();
        fixture.Files.Setup(f => f.Read(It.IsAny<string>(), It.IsAny<string>())).Returns("{ not json"u8.ToArray());
        var sut = fixture.CreateSut();

        // Act
        var read = sut.TryRead(Guid.NewGuid());

        // Assert
        Assert.Null(read);
    }

    [Fact]
    public void Delete_SessionOpen_DeletesTheFileOfThatTab()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        fixture.OpenSession();
        var tab = Guid.NewGuid();
        var sut = fixture.CreateSut();

        // Act
        sut.Delete(tab);

        // Assert
        fixture.Files.Verify(f => f.DeleteFile(SessionSpillStoreFixture.SpillFolder, SessionSpillStoreFixture.FileNameOf(tab)), Times.Once);
        fixture.Files.VerifyNoOtherCalls();
    }

    [Fact]
    public void DeleteAll_SessionOpen_DeletesTheSpillFolder()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        fixture.OpenSession();
        var sut = fixture.CreateSut();

        // Act
        sut.DeleteAll();

        // Assert
        fixture.Files.Verify(f => f.DeleteFolder(SessionSpillStoreFixture.SpillFolder), Times.Once);
    }

    [Fact]
    public void DeleteAll_SessionNotOpen_DoesNothing()
    {
        // Arrange
        var fixture = new SessionSpillStoreFixture();
        var sut = fixture.CreateSut();

        // Act
        sut.DeleteAll();
        sut.Delete(Guid.NewGuid());

        // Assert
        fixture.Files.VerifyNoOtherCalls();
    }
}
