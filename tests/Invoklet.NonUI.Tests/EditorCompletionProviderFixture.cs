using AvaloniaEdit.CodeCompletion;
using Invoklet.Contracts.IntelliSense;
using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class EditorCompletionProviderFixture
{
    public const string Text = "Console.Wr";

    public Mock<IIntelliSenseService> IntelliSense { get; } = new();

    public Mock<ICompletionItemFactory> Items { get; } = new();

    public void SetupSignatureHelp(bool argumentSlotEmpty)
    {
        var help = new Mock<ISignatureHelpResult>();
        help.SetupGet(h => h.IsArgumentSlotEmpty).Returns(argumentSlotEmpty);
        IntelliSense.Setup(i => i.GetSignatureHelpAsync(Text, It.IsAny<int>(), It.IsAny<char?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(help.Object);
    }

    /// <summary>Makes the language service offer these items and the factory turn each into a row.</summary>
    public List<ICompletionData> SetupCompletions(int count)
    {
        var rows = new List<ICompletionData>();
        var items = new List<ICompletionItem>();
        for (var i = 0; i < count; i++)
        {
            var item = new Mock<ICompletionItem>().Object;
            var row = new Mock<ICompletionData>().Object;
            Items.Setup(f => f.Create(item)).Returns(row);
            items.Add(item);
            rows.Add(row);
        }

        var list = new Mock<ICompletionList>();
        list.SetupGet(l => l.Items).Returns(items);
        IntelliSense.Setup(i => i.GetCompletionsAsync(Text, It.IsAny<int>(), It.IsAny<CompletionTrigger>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(list.Object);
        return rows;
    }

    public EditorCompletionProvider CreateSut() => new(IntelliSense.Object, () => Text, Items.Object);
}
