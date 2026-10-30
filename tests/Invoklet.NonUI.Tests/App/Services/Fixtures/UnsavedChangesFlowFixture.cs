using Invoklet.Format.MdRun;
using Invoklet.Services;
using Invoklet.Services.Preferences;
using Invoklet.UserControls.Models;
using Moq;

namespace Invoklet.NonUI.Tests;

internal sealed class UnsavedChangesFlowFixture
{
    public Mock<IWindowService> Windows { get; } = new();

    public List<string> Calls { get; } = [];

    /// <summary>What the in-place save answers for each tab; a tab not listed saves.</summary>
    public Dictionary<EditorTabItemViewModel, InPlaceSaveResult> SaveResults { get; } = [];

    /// <summary>Whether the Save As of a tab clears its dirty mark, as a Save As that wrote does.</summary>
    public bool SaveAsWrites { get; set; } = true;

    public UnsavedChangesFlowFixture()
    {
        Windows.Setup(w => w.ShowErrorAsync(It.IsAny<string>(), It.IsAny<string>())).Returns(Task.CompletedTask);
    }

    public void UserChooses(UnsavedChangesResult choice)
        => Windows.Setup(w => w.ShowUnsavedChangesDialogAsync(It.IsAny<string>())).ReturnsAsync(choice);

    public EditorTabItemViewModel CreateTab(string? filePath, bool dirty)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        var tab = new EditorTabItemViewModel(filePath, [], prefs.Object, new Mock<IEditorTabActions>().Object,
            MdRunLanguage.CSharp, MdRunKind.Statements, filePath is null ? "Untitled1" : "Untitled")
        {
            IsDirty = dirty,
        };
        return tab;
    }

    public UnsavedChangesFlow CreateSut() => new(
        Windows.Object,
        tab =>
        {
            Calls.Add("save " + tab.FileName);
            return Task.FromResult(SaveResults.GetValueOrDefault(tab, InPlaceSaveResult.Saved));
        },
        tab =>
        {
            Calls.Add("saveAs " + tab.FileName);
            if (SaveAsWrites) tab.IsDirty = false;
            return Task.CompletedTask;
        });
}
