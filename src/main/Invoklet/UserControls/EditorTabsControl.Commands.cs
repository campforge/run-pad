using Invoklet.Services.Scaling;
using Invoklet.Services.Tabs;
using Invoklet.UserControls.Models;

namespace Invoklet.UserControls;

// The strip answers which tab is being looked at; what the commands do with it is ActiveTabCommands.
public partial class EditorTabsControl
{
    private ActiveTabCommands? _commands;

    /// <summary>The commands of the keys and the menu on the tab being looked at.</summary>
    internal ActiveTabCommands Commands => _commands ??= new ActiveTabCommands(
        this,
        () => _prefs is null ? null : ViewScale.For(_prefs),
        tab => _diskSync?.RevertAsync(this, tab) ?? Task.CompletedTask);

    EditorTabItemViewModel? IActiveTabSource.ViewModel => _vm?.ActiveTab;

    ITabCommandTarget? IActiveTabSource.Target => GetActiveControl();

    EditorTabItemViewModel? ITabDiskHost.ActiveTab => _vm?.ActiveTab;
}
