using Invoklet.Services;
using Moq;

namespace Invoklet.NonUI.Tests.App.Services.Fixtures;

internal sealed class ResultsExporterFixture
{
    public Mock<IWindowService> Windows { get; } = new();

    public string Css { get; set; } = "body { color: red; }";

    public string Content { get; set; } = "<div class=\"out\">hello</div>";

    public string? ScriptPath { get; set; } = Path.Combine("scripts", "Report.inv");

    public string? ChosenPath { get; set; } = "chosen.html";

    public Exception? WriteFails { get; set; }

    public List<(string Path, string Text)> Written { get; } = [];

    public ResultsExporter CreateSut()
    {
        Windows.Setup(w => w.ShowSaveHtmlFileDialogAsync(It.IsAny<string>(), It.IsAny<string>())).ReturnsAsync(() => ChosenPath);
        return new ResultsExporter(Windows.Object, () => Css, () => Content, () => ScriptPath, () => "documents", (path, text, _) =>
        {
            if (WriteFails is not null) throw WriteFails;
            Written.Add((path, text));
            return Task.CompletedTask;
        });
    }
}
