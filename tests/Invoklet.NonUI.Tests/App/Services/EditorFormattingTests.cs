using Invoklet.Contracts.IntelliSense;
using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class EditorFormattingTests
{
    private static readonly IReadOnlyList<TextEdit> OneEdit = [new TextEdit(5, 0, " ")];

    [Fact]
    public async Task FormatSelectionAsync_TheServiceAnswers_AppliesTheEditsOnce()
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        fixture.Service.Setup(s => s.FormatRangeAsync("var a=1;", 4, 1, It.IsAny<CancellationToken>())).ReturnsAsync(OneEdit);
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatSelectionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(OneEdit, Assert.Single(fixture.Applied));
    }

    [Fact]
    public async Task FormatSelectionAsync_NothingToChange_AppliesNothing()
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        fixture.Service.Setup(s => s.FormatRangeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatSelectionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task FormatSelectionAsync_TheTextChangedWhileTheServiceWorked_DropsTheAnswer()
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        fixture.Service.Setup(s => s.FormatRangeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                fixture.Version = new object();
                return Task.FromResult<IReadOnlyList<TextEdit>?>(OneEdit);
            });
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatSelectionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task FormatSelectionAsync_NoLanguageService_DoesNothing()
    {
        // Arrange
        var fixture = new EditorFormattingFixture { HasService = false };
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatSelectionAsync(TestContext.Current.CancellationToken);

        // Assert
        Assert.Empty(fixture.Applied);
        fixture.Target.Verify(t => t.Apply(It.IsAny<IReadOnlyList<TextEdit>>()), Times.Never);
    }

    [Theory]
    [InlineData(';')]
    [InlineData('}')]
    public async Task FormatOnTypeAsync_ACharacterFormattedAfter_AsksWithTheCaretAndAppliesTheAnswer(char typed)
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        fixture.Service.Setup(s => s.FormatOnTypeAsync("var a=1;", 8, typed, It.IsAny<CancellationToken>())).ReturnsAsync(OneEdit);
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatOnTypeAsync(typed);

        // Assert
        Assert.Equal(OneEdit, Assert.Single(fixture.Applied));
    }

    [Theory]
    [InlineData('a')]
    [InlineData(' ')]
    [InlineData('{')]
    public async Task FormatOnTypeAsync_AnyOtherCharacter_NeverAsksTheService(char typed)
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatOnTypeAsync(typed);

        // Assert
        fixture.Service.Verify(s => s.FormatOnTypeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<char>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FormatOnTypeAsync_SwitchedOff_NeverAsksTheService()
    {
        // Arrange
        var fixture = new EditorFormattingFixture { FormatOnType = false };
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatOnTypeAsync(';');

        // Assert
        fixture.Service.Verify(s => s.FormatOnTypeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<char>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task FormatOnTypeAsync_TheTextChangedMeanwhile_DropsTheAnswer()
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        fixture.Service.Setup(s => s.FormatOnTypeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<char>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                fixture.Version = new object();
                return Task.FromResult<IReadOnlyList<TextEdit>?>(OneEdit);
            });
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatOnTypeAsync(';');

        // Assert
        Assert.Empty(fixture.Applied);
    }

    [Fact]
    public async Task FormatOnTypeAsync_ANewerCharacterWhileTheFirstIsWorking_SupersedesIt()
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        var first = new TaskCompletionSource<IReadOnlyList<TextEdit>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        fixture.Service.Setup(s => s.FormatOnTypeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<char>(), It.IsAny<CancellationToken>()))
            .Returns(() => ++calls == 1 ? first.Task : Task.FromResult<IReadOnlyList<TextEdit>?>([new TextEdit(0, 0, "second")]));
        var sut = fixture.CreateSut();

        // Act
        var firstRun = sut.FormatOnTypeAsync(';');
        await sut.FormatOnTypeAsync('}');
        first.SetResult(OneEdit);
        await firstRun;

        // Assert
        Assert.Equal("second", Assert.Single(fixture.Applied)[0].NewText);
    }

    [Fact]
    public async Task FormatOnTypeAsync_TheServiceFindsNothing_AppliesNothing()
    {
        // Arrange
        var fixture = new EditorFormattingFixture();
        fixture.Service.Setup(s => s.FormatOnTypeAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<char>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<TextEdit>?)null);
        var sut = fixture.CreateSut();

        // Act
        await sut.FormatOnTypeAsync(';');

        // Assert
        Assert.Empty(fixture.Applied);
    }
}
