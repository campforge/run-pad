using Invoklet.Contracts.IntelliSense;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

internal sealed class EditorFormattingFixture
{
    public Mock<IIntelliSenseService> Service { get; } = new();

    public Mock<IFormattingTarget> Target { get; } = new();

    public bool FormatOnType { get; set; } = true;

    public bool HasService { get; set; } = true;

    public object Version { get; set; } = new();

    public List<IReadOnlyList<TextEdit>> Applied { get; } = [];

    public EditorFormattingFixture()
    {
        Target.Setup(x => x.Text).Returns("var a=1;");
        Target.Setup(x => x.CaretOffset).Returns(8);
        Target.Setup(x => x.Selection).Returns((4, 1));
        Target.Setup(x => x.CaptureVersion()).Returns(() => Version);
        Target.Setup(x => x.IsUnchangedSince(It.IsAny<object>())).Returns<object>(v => ReferenceEquals(v, Version));
        Target.Setup(x => x.Apply(It.IsAny<IReadOnlyList<TextEdit>>())).Callback<IReadOnlyList<TextEdit>>(Applied.Add);
    }

    public EditorFormatting CreateSut() => new(() => HasService ? Service.Object : null, Target.Object, () => FormatOnType);
}
