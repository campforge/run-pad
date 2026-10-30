using Invoklet.Contracts.Models;
using Invoklet.Format.MdRun;
using Invoklet.Services.Preferences;
using Invoklet.Services.Scaling;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class ActiveTabCommandsFixture
{
    private readonly TaskCompletionSource _saveAsRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _closeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ActiveTabCommandsFixture()
    {
        // The tab's own commands end in these two calls; the fixture signals when one is made.
        TabActions.Setup(a => a.SaveAsAsync(It.IsAny<EditorTabItemViewModel>()))
            .Callback(() => _saveAsRequested.TrySetResult())
            .Returns(Task.CompletedTask);
        TabActions.Setup(a => a.CloseAsync(It.IsAny<EditorTabItemViewModel>()))
            .Callback(() => _closeRequested.TrySetResult())
            .Returns(Task.CompletedTask);
        Reverter.Setup(r => r.RevertAsync(It.IsAny<EditorTabItemViewModel>())).Returns(Task.CompletedTask);
    }

    public Mock<ITabCommandTarget> Target { get; } = new();

    public Mock<IActiveTabSource> Source { get; } = new();

    public Mock<ITabReverter> Reverter { get; } = new();

    public Mock<ICodeZoom> Zoom { get; } = new();

    public Mock<IEditorTabActions> TabActions { get; } = new();

    /// <summary>Completes when the tab's Save As command reached its action.</summary>
    public Task SaveAsRequested => _saveAsRequested.Task;

    /// <summary>Completes when the tab's close command reached its action.</summary>
    public Task CloseRequested => _closeRequested.Task;

    public DotnetSdk CreateSdk(string name = "net10") => new(name, "net10.0", "10.0.0", name);

    public EditorTabItemViewModel CreateTab(string? path = "/scripts/a.inv", bool withSdk = true)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        List<DotnetSdk> sdks = withSdk ? [CreateSdk()] : [];
        return new EditorTabItemViewModel(path, sdks, prefs.Object, TabActions.Object, MdRunLanguage.CSharp, MdRunKind.Statements);
    }

    /// <summary>Makes <paramref name="tab"/> the active one; <paramref name="withView"/> false is a tab that has no view yet.</summary>
    public void Activate(EditorTabItemViewModel? tab, bool withView = true)
    {
        Source.SetupGet(s => s.ViewModel).Returns(tab);
        Source.SetupGet(s => s.Target).Returns(withView && tab is not null ? Target.Object : null);
    }

    public void ProvideCodeZoom() => Source.SetupGet(s => s.CodeZoom).Returns(Zoom.Object);

    public ActiveTabCommands CreateSut() => new(Source.Object, Reverter.Object);
}
