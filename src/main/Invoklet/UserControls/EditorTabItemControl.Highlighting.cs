namespace Invoklet.UserControls;

public partial class EditorsTabItemControl
{
    // A tab that is not shown has no use for syntax colouring, and its tokenizer holds a thread-pool
    // thread for as long as it runs: the tabs not being used are frozen, keeping what they are coloured
    // with, and the one the user selects is resumed.
    internal void SetHighlightingActive(bool active)
    {
        if (_disposed) return;
        if (active)
        {
            State.Editor.ResumeHighlighting();
            _ilHighlighting?.Resume();
        }
        else
        {
            State.Editor.FreezeHighlighting();
            _ilHighlighting?.Freeze();
        }
    }
}
