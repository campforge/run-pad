using Invoklet.Contracts.IntelliSense;
using Invoklet.Languages.DotnetCommon;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class SymbolNavigatorFixture
{
    public Mock<IIntelliSenseService> IntelliSense { get; } = new();

    public Mock<IWindowService> Windows { get; } = new();

    public Mock<INavigationTarget> Target { get; } = new();

    public Mock<Func<IDefinitionResult, DecompiledMemberResult>> Decompile { get; } = new();

    public SymbolNavigatorFixture()
    {
        Target.SetupGet(t => t.Text).Returns("code");
        Target.SetupGet(t => t.CaretOffset).Returns(4);
    }

    public static IImplementationLocation CreateImplementation(int offset)
    {
        var implementation = new Mock<IImplementationLocation>();
        implementation.SetupGet(i => i.Offset).Returns(offset);
        return implementation.Object;
    }

    public Mock<IDefinitionResult> SetupDefinition(bool inSource = false, int offset = 0)
    {
        var definition = new Mock<IDefinitionResult>();
        definition.SetupGet(d => d.IsInSource).Returns(inSource);
        definition.SetupGet(d => d.Offset).Returns(offset);
        definition.SetupGet(d => d.DisplayName).Returns("Name");
        IntelliSense.Setup(i => i.GetDefinitionAsync("code", 4, It.IsAny<CancellationToken>())).ReturnsAsync(definition.Object);
        return definition;
    }

    public SymbolNavigator CreateSut() => new(IntelliSense.Object, Windows.Object, Decompile.Object);
}
