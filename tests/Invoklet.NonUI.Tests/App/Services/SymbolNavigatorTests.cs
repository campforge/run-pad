using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.DotnetCommon;
using Invoklet.NonUI.Tests.App.Services.Fixtures;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests.App.Services;

public sealed class SymbolNavigatorTests
{
    [Fact]
    public async Task GoToDefinitionAsync_OneImplementation_JumpsToIt()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        fixture.IntelliSense.Setup(i => i.GetImplementationsAsync("code", 4, It.IsAny<CancellationToken>()))
            .ReturnsAsync([SymbolNavigatorFixture.CreateImplementation(12)]);
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Target.Verify(t => t.JumpTo(12), Times.Once);
        fixture.IntelliSense.Verify(i => i.GetDefinitionAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GoToDefinitionAsync_SeveralImplementations_OffersThem()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        IReadOnlyList<IImplementationLocation> implementations =
            [SymbolNavigatorFixture.CreateImplementation(5), SymbolNavigatorFixture.CreateImplementation(9)];
        fixture.IntelliSense.Setup(i => i.GetImplementationsAsync("code", 4, It.IsAny<CancellationToken>())).ReturnsAsync(implementations);
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Target.Verify(t => t.ShowImplementations(implementations), Times.Once);
        fixture.Target.Verify(t => t.JumpTo(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GoToDefinitionAsync_NoDefinition_DoesNothing()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Target.Verify(t => t.JumpTo(It.IsAny<int>()), Times.Never);
        fixture.Windows.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GoToDefinitionAsync_DefinitionInTheScript_JumpsToIt()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        fixture.SetupDefinition(inSource: true, offset: 30);
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Target.Verify(t => t.JumpTo(30), Times.Once);
        fixture.Windows.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GoToDefinitionAsync_GeneratedSource_ShowsItWithoutDecompiling()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        var definition = fixture.SetupDefinition();
        definition.SetupGet(d => d.GeneratedDocument).Returns(("class Client {}", 3));
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Windows.Verify(w => w.ShowDefinitionInAssemblyExplorerAsync(
            It.Is<DecompiledMemberResult>(r => r.Text == "class Client {}" && r.Line == 3 && r.Title == "Name"), definition.Object), Times.Once);
        fixture.Decompile.Verify(d => d(It.IsAny<IDefinitionResult>()), Times.Never);
    }

    [Fact]
    public async Task GoToDefinitionAsync_DefinitionInALibrary_ShowsTheDecompiledMember()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        var definition = fixture.SetupDefinition();
        var decompiled = new DecompiledMemberResult("class C {}", 1, "C");
        fixture.Decompile.Setup(d => d(definition.Object)).Returns(decompiled);
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Windows.Verify(w => w.ShowDefinitionInAssemblyExplorerAsync(decompiled, definition.Object), Times.Once);
    }

    [Fact]
    public async Task GoToDefinitionAsync_DecompilerFails_ShowsTheFailureInPlaceOfTheSource()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        var definition = fixture.SetupDefinition();
        fixture.Decompile.Setup(d => d(It.IsAny<IDefinitionResult>())).Throws(new InvalidOperationException("no pdb"));
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Windows.Verify(w => w.ShowDefinitionInAssemblyExplorerAsync(
            It.Is<DecompiledMemberResult>(r => r.Text == "// Failed to decompile: no pdb" && r.Title == "Name"), definition.Object), Times.Once);
    }

    [Fact]
    public async Task FindReferencesAsync_SymbolOccurs_OffersTheOccurrences()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        IReadOnlyList<IDocumentHighlightSpan> highlights = [new Mock<IDocumentHighlightSpan>().Object];
        fixture.IntelliSense.Setup(i => i.GetDocumentHighlightsAsync("code", 4, It.IsAny<CancellationToken>())).ReturnsAsync(highlights);
        var sut = fixture.CreateSut();

        // Act
        await sut.FindReferencesAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Target.Verify(t => t.ShowReferences(highlights), Times.Once);
    }

    [Fact]
    public async Task FindReferencesAsync_NothingFound_OffersNothing()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        fixture.IntelliSense.Setup(i => i.GetDocumentHighlightsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var sut = fixture.CreateSut();

        // Act
        await sut.FindReferencesAsync(fixture.Target.Object, TestContext.Current.CancellationToken);

        // Assert
        fixture.Target.Verify(t => t.ShowReferences(It.IsAny<IReadOnlyList<IDocumentHighlightSpan>>()), Times.Never);
    }

    [Fact]
    public async Task GoToDefinitionAsync_CancelledWhileLookingForImplementations_ThrowsAndTouchesNothing()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.IntelliSense.Setup(i => i.GetImplementationsAsync("code", 4, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel(); // the tab closed, or a newer request replaced this one
                return Task.FromResult<IReadOnlyList<IImplementationLocation>?>([SymbolNavigatorFixture.CreateImplementation(12)]);
            });
        var sut = fixture.CreateSut();

        // Act
        var navigate = () => sut.GoToDefinitionAsync(fixture.Target.Object, cancellation.Token);

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(navigate);
        fixture.Target.Verify(t => t.JumpTo(It.IsAny<int>()), Times.Never);
    }

    [Fact]
    public async Task GoToDefinitionAsync_CancelledWhileDecompiling_ThrowsAndOpensNoWindow()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.SetupDefinition();
        fixture.Decompile.Setup(d => d(It.IsAny<IDefinitionResult>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                return new DecompiledMemberResult("class C {}", 1, "C");
            });
        var sut = fixture.CreateSut();

        // Act
        var navigate = () => sut.GoToDefinitionAsync(fixture.Target.Object, cancellation.Token);

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(navigate);
        fixture.Windows.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task FindReferencesAsync_CancelledWhileSearching_ThrowsAndOffersNothing()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        using var cancellation = new CancellationTokenSource();
        fixture.IntelliSense.Setup(i => i.GetDocumentHighlightsAsync("code", 4, It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                cancellation.Cancel();
                return Task.FromResult<IReadOnlyList<IDocumentHighlightSpan>?>([new Mock<IDocumentHighlightSpan>().Object]);
            });
        var sut = fixture.CreateSut();

        // Act
        var search = () => sut.FindReferencesAsync(fixture.Target.Object, cancellation.Token);

        // Assert
        await Assert.ThrowsAsync<OperationCanceledException>(search);
        fixture.Target.Verify(t => t.ShowReferences(It.IsAny<IReadOnlyList<IDocumentHighlightSpan>>()), Times.Never);
    }

    [Fact]
    public async Task GoToDefinitionAsync_Called_PassesItsTokenToTheLookups()
    {
        // Arrange
        var fixture = new SymbolNavigatorFixture();
        using var cancellation = new CancellationTokenSource();
        var sut = fixture.CreateSut();

        // Act
        await sut.GoToDefinitionAsync(fixture.Target.Object, cancellation.Token);

        // Assert
        fixture.IntelliSense.Verify(i => i.GetImplementationsAsync("code", 4, cancellation.Token), Times.Once);
        fixture.IntelliSense.Verify(i => i.GetDefinitionAsync("code", 4, cancellation.Token), Times.Once);
    }
}
