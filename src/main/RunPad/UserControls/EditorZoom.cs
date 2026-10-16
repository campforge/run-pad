namespace RunPad.UserControls;

// The steps of the code zoom (EditorSettings.ZoomPercent, shared by every editor): the wheel, the
// View menu and the status-bar menu all move through them. Pure functions so the step math is
// testable without a live editor.
internal static class EditorZoom
{
    // Browser-style steps.
    public static readonly IReadOnlyList<int> Steps = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300];

    // The first step strictly above the current percent — a settings file can hold a zoom between
    // steps (e.g. 108%), and "Zoom In" from there should land on 110%, not skip to 125%. Already past
    // the last step stays put rather than jumping back the other way.
    public static int NextStep(int percent) =>
        Steps.FirstOrDefault(s => s > percent, percent);

    public static int PreviousStep(int percent) =>
        Steps.LastOrDefault(s => s < percent, percent);
}
