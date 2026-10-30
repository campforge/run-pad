using Invoklet.Format.MdRun;
using Invoklet.Services.Preferences;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;
using Moq;

namespace Invoklet.NonUI.Tests.App.Services.Tabs.Fixtures;

/// <summary>The view of a tab as a mock that remembers whether its content is released.</summary>
internal sealed class TabViewDouble
{
    public TabViewDouble()
    {
        View.SetupGet(v => v.IsContentReleased).Returns(() => Released);
        View.SetupGet(v => v.HasRunningProcess).Returns(() => Running);
        View.Setup(v => v.TryReleaseContent()).Returns(() =>
        {
            if (!CanRelease) return false;
            Released = true;
            return true;
        });
        View.Setup(v => v.TryRestoreContentAsync()).Returns(async () =>
        {
            if (RestoreGate is { } gate) await gate.Task;
            if (!CanRestore) return false;
            Released = false;
            return true;
        });
    }

    public Mock<ITabView> View { get; } = new();

    public bool Released { get; set; }

    public bool Running { get; set; }

    public bool CanRelease { get; set; } = true;

    public bool CanRestore { get; set; } = true;

    /// <summary>When set, the restore ends when this does.</summary>
    public TaskCompletionSource<bool>? RestoreGate { get; set; }
}

internal sealed class TabLifecycleManagerFixture
{
    private readonly List<EditorTabItemViewModel> _givenUp = [];

    public TabLifecycleManagerFixture()
    {
        // The policy decides which tabs it gives up; what the manager adds is the predicate it passes, which the mock applies.
        Policy.Setup(p => p.ColdTabs(It.IsAny<IEnumerable<EditorTabItemViewModel>>(), It.IsAny<Func<EditorTabItemViewModel, bool>>()))
            .Returns((IEnumerable<EditorTabItemViewModel> loaded, Func<EditorTabItemViewModel, bool> canUnload)
                => loaded.Where(tab => _givenUp.Contains(tab) && canUnload(tab)).ToList());
    }

    public Mock<IResidencyPolicy> Policy { get; } = new();

    public Mock<ITabSpillStore> Spill { get; } = new();

    public Dictionary<EditorTabItemViewModel, TabViewDouble> Views { get; } = new();

    public EditorTabItemViewModel? Active { get; set; }

    public List<EditorTabItemViewModel> NotRestored { get; } = [];

    public Func<EditorTabItemViewModel?> ActiveTabSource => () => Active;

    public Action<EditorTabItemViewModel> OnNotRestored => NotRestored.Add;

    public EditorTabItemViewModel CreateTab(string name)
    {
        var prefs = new Mock<IPreferencesService>();
        prefs.Setup(p => p.Layout).Returns(new LayoutSettings());
        prefs.Setup(p => p.Keyboard).Returns(new KeyboardSettings());
        prefs.Setup(p => p.Editor).Returns(new EditorSettings());
        prefs.Setup(p => p.CSharp).Returns(new CSharpLanguageSettings());
        prefs.Setup(p => p.Session).Returns(new SessionSettings());
        var tab = new EditorTabItemViewModel(null, [], prefs.Object, new Mock<IEditorTabActions>().Object,
            MdRunLanguage.CSharp, MdRunKind.Statements, name);
        Views[tab] = new TabViewDouble();
        return tab;
    }

    /// <summary>The tabs the policy gives up when asked.</summary>
    public void PolicyGivesUp(params EditorTabItemViewModel[] tabs) => _givenUp.AddRange(tabs);

    public TabLifecycleManager CreateSut() => new(Policy.Object, Spill.Object);
}
