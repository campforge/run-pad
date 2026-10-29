namespace Invoklet.UserControls.Models;

// The real behavior behind EditorTabItemViewModel's Close/SaveAs/Duplicate/OpenContainingFolder
// commands — implemented by MainWindowViewModel, which owns the tab
// bookkeeping (Tabs/_openTabs) and dialogs (IWindowService) these actions need. Kept as a narrow
// interface rather than a MainWindowViewModel reference so the tab's own ViewModel only depends
// on the four operations its commands can trigger, not the shell VM's full surface.
public interface IEditorTabActions
{
    Task CloseAsync(EditorTabItemViewModel tab);
    Task SaveAsAsync(EditorTabItemViewModel tab);
    void Duplicate(EditorTabItemViewModel tab);
    void OpenContainingFolder(EditorTabItemViewModel tab);
}
