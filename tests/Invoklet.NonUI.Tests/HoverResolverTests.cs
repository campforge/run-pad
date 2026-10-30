using Invoklet.Contracts.Models;
using Invoklet.Services;
using Moq;
using Xunit;

namespace Invoklet.NonUI.Tests;

public sealed class HoverResolverTests
{
    [Fact]
    public async Task ResolveAsync_SymbolIsALocalOfTheStop_ShowsItsValueWithoutEvaluating()
    {
        // Arrange
        var fixture = new HoverResolverFixture();
        var context = new Mock<IBreakpointHitContext>().Object;
        var local = HoverResolverFixture.CreateVariable("total");
        var request = fixture.CreateRequest(HoverResolverFixture.Inside("total"), context, [local]);
        var sut = fixture.CreateSut();

        // Act
        var hover = await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        var value = Assert.IsType<DebugValueHover>(hover);
        Assert.Equal("total", value.Symbol);
        Assert.Equal(HoverResolverFixture.Text.IndexOf("total", StringComparison.Ordinal), value.SymbolStart);
        Assert.Same(local, value.Value);
        fixture.Debug.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_SymbolIsNotALocal_EvaluatesItInThePausedProgram()
    {
        // Arrange
        var fixture = new HoverResolverFixture();
        var context = new Mock<IBreakpointHitContext>().Object;
        var evaluated = HoverResolverFixture.CreateVariable("count", "3");
        fixture.Debug.Setup(d => d.EvaluateHoverAsync(context, "count", It.IsAny<CancellationToken>())).ReturnsAsync(evaluated);
        var request = fixture.CreateRequest(HoverResolverFixture.Inside("count"), context, []);
        var sut = fixture.CreateSut();

        // Act
        var hover = await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(evaluated, Assert.IsType<DebugValueHover>(hover).Value);
    }

    [Fact]
    public async Task ResolveAsync_StopIsNotCurrentAnymore_DoesNotEvaluateAndFallsBackToQuickInfo()
    {
        // Arrange
        var fixture = new HoverResolverFixture { ContextIsCurrent = false };
        var context = new Mock<IBreakpointHitContext>().Object;
        var info = fixture.SetupQuickInfo();
        var request = fixture.CreateRequest(HoverResolverFixture.Inside("count"), context, []);
        var sut = fixture.CreateSut();

        // Act
        var hover = await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(info, Assert.IsType<QuickInfoHover>(hover).Info);
        fixture.Debug.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_NotPaused_ShowsQuickInfo()
    {
        // Arrange
        var fixture = new HoverResolverFixture();
        var info = fixture.SetupQuickInfo();
        var request = fixture.CreateRequest(HoverResolverFixture.Inside("count"));
        var sut = fixture.CreateSut();

        // Act
        var hover = await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(info, Assert.IsType<QuickInfoHover>(hover).Info);
        fixture.Debug.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_DiagnosticUnderThePointer_ShowsItInsteadOfQuickInfo()
    {
        // Arrange
        var fixture = new HoverResolverFixture();
        var offset = HoverResolverFixture.Inside("count");
        var diagnostic = HoverResolverFixture.CreateDiagnostic(offset - 1, offset + 5, "CS0103: no such name");
        var request = fixture.CreateRequest(offset, diagnostics: diagnostic);
        var sut = fixture.CreateSut();

        // Act
        var hover = await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("CS0103: no such name", Assert.IsType<DiagnosticHover>(hover).Message);
        fixture.IntelliSense.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveAsync_DiagnosticElsewhere_IsIgnored()
    {
        // Arrange
        var fixture = new HoverResolverFixture();
        var info = fixture.SetupQuickInfo();
        var diagnostic = HoverResolverFixture.CreateDiagnostic(0, 3, "elsewhere");
        var request = fixture.CreateRequest(HoverResolverFixture.Inside("count"), diagnostics: diagnostic);
        var sut = fixture.CreateSut();

        // Act
        var hover = await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Same(info, Assert.IsType<QuickInfoHover>(hover).Info);
    }

    [Fact]
    public async Task ResolveAsync_NothingToSay_ReturnsNull()
    {
        // Arrange
        var fixture = new HoverResolverFixture();
        var request = fixture.CreateRequest(HoverResolverFixture.Inside("count"));
        var sut = fixture.CreateSut();

        // Act
        var hover = await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        Assert.Null(hover);
    }

    [Fact]
    public async Task ResolveAsync_PointerOnASpace_NeverAsksTheDebuggerForASymbol()
    {
        // Arrange
        var fixture = new HoverResolverFixture();
        var context = new Mock<IBreakpointHitContext>().Object;
        var request = fixture.CreateRequest(HoverResolverFixture.Text.IndexOf(' '), context, []);
        var sut = fixture.CreateSut();

        // Act
        await sut.ResolveAsync(request, TestContext.Current.CancellationToken);

        // Assert
        fixture.Debug.VerifyNoOtherCalls();
    }
}
