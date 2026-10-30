using Invoklet.Avalonia.CodeTextEditor.Completions;
using Invoklet.Contracts.IntelliSense;
using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class EditorCompletionProviderTests
{
    [Fact]
    public async Task ProvideAsync_CtrlSpaceInAnEmptyArgumentSlot_RedirectsToOverloadsWithoutListing()
    {
        // Arrange
        var fixture = new EditorCompletionProviderFixture();
        fixture.SetupSignatureHelp(argumentSlotEmpty: true);
        var request = new CompletionRequestedEventArgs('\0', 10, isExplicitInvoke: true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProvideAsync(request);

        // Assert
        Assert.True(request.RedirectToOverloads);
        Assert.Empty(request.Items);
        fixture.IntelliSense.Verify(i => i.GetCompletionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CompletionTrigger>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProvideAsync_CtrlSpaceElsewhere_ListsTheCompletionsOfAnExplicitInvoke()
    {
        // Arrange
        var fixture = new EditorCompletionProviderFixture();
        fixture.SetupSignatureHelp(argumentSlotEmpty: false);
        var rows = fixture.SetupCompletions(2);
        var request = new CompletionRequestedEventArgs('\0', 10, isExplicitInvoke: true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProvideAsync(request);

        // Assert
        Assert.False(request.RedirectToOverloads);
        Assert.Equal(rows, request.Items);
        fixture.IntelliSense.Verify(i => i.GetCompletionsAsync(EditorCompletionProviderFixture.Text, 10, CompletionTrigger.Invoked, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ProvideAsync_TypedTriggerCharacter_ListsTheCompletionsOfThatInsertionWithoutProbingSignatureHelp()
    {
        // Arrange
        var fixture = new EditorCompletionProviderFixture();
        var rows = fixture.SetupCompletions(1);
        var request = new CompletionRequestedEventArgs('.', 8);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProvideAsync(request);

        // Assert
        Assert.Equal(rows, request.Items);
        fixture.IntelliSense.Verify(i => i.GetCompletionsAsync(EditorCompletionProviderFixture.Text, 8, CompletionTrigger.Insertion('.'), It.IsAny<CancellationToken>()), Times.Once);
        fixture.IntelliSense.Verify(i => i.GetSignatureHelpAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<char?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProvideAsync_AutomaticInvokeWithATypedCharacter_DoesNotProbeSignatureHelp()
    {
        // Arrange
        var fixture = new EditorCompletionProviderFixture();
        fixture.SetupCompletions(1);
        var request = new CompletionRequestedEventArgs('r', 10, isExplicitInvoke: true);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProvideAsync(request);

        // Assert
        fixture.IntelliSense.Verify(i => i.GetSignatureHelpAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<char?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ProvideAsync_ServiceFindsThePlaceNotWorthCompleting_SuppressesSnippetsToo()
    {
        // Arrange
        var fixture = new EditorCompletionProviderFixture();
        fixture.IntelliSense.Setup(i => i.GetCompletionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CompletionTrigger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ICompletionList?)null);
        var request = new CompletionRequestedEventArgs('.', 8);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProvideAsync(request);

        // Assert
        Assert.True(request.SuppressSnippets);
        Assert.Empty(request.Items);
    }

    [Fact]
    public async Task ProvideAsync_RequestCancelled_PassesItsTokenToTheService()
    {
        // Arrange
        var fixture = new EditorCompletionProviderFixture();
        fixture.SetupCompletions(0);
        using var cancellation = new CancellationTokenSource();
        var request = new CompletionRequestedEventArgs('.', 8, cancellationToken: cancellation.Token);
        var sut = fixture.CreateSut();

        // Act
        await sut.ProvideAsync(request);

        // Assert
        fixture.IntelliSense.Verify(i => i.GetCompletionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CompletionTrigger>(), cancellation.Token), Times.Once);
    }
}
