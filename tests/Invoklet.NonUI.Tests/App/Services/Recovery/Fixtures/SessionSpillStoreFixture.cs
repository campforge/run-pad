using System.Text.Json;
using Invoklet.Services.Recovery;
using Invoklet.UserControls.Models;
using Moq;

namespace Invoklet.NonUI.Tests.App.Services.Recovery.Fixtures;

internal sealed class SessionSpillStoreFixture
{
    public const string SessionFolder = "/recovery/session";

    public Mock<ISessionFolder> Session { get; } = new();

    public Mock<ISpillFiles> Files { get; } = new();

    public static string SpillFolder => Path.Combine(SessionFolder, "spill");

    public static string FileNameOf(Guid tab) => tab.ToString("N") + ".json";

    public void OpenSession() => Session.SetupGet(s => s.SessionFolder).Returns(SessionFolder);

    public ReleasedContent CreateContent(string text = "var a = 1;", TabOutputState? output = null)
        => new(text, new EditorViewState(4, 1, 2, 7.5), output);

    public byte[] Serialize(ReleasedContent content) => JsonSerializer.SerializeToUtf8Bytes(content);

    public SessionSpillStore CreateSut() => new(Session.Object, Files.Object);
}
