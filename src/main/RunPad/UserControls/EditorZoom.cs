namespace RunPad.UserControls;

// Editor zoom is just the CodeTextEditor's FontSize relative to the Preferences font size — Ctrl+wheel
// (CodeTextEditor.OnFontSizeScroll) changes FontSize directly in 1pt steps, so the percentage is
// derived from the two sizes rather than stored separately. Pure functions so the step/percent math is
// testable without a live editor.
internal static class EditorZoom
{
    // Browser-style steps used by the status-bar zoom menu's "Zoom In"/"Zoom Out".
    public static readonly IReadOnlyList<int> Steps = [50, 67, 75, 80, 90, 100, 110, 125, 150, 175, 200, 250, 300];

    public static int ToPercent(double fontSize, double baseFontSize) =>
        baseFontSize > 0 ? (int)Math.Round(fontSize / baseFontSize * 100) : 100;

    public static double ToFontSize(int percent, double baseFontSize) =>
        Math.Max(1, baseFontSize * percent / 100);

    // The first step strictly above the current percent — Ctrl+wheel can leave the zoom between
    // steps (e.g. 108%), and "Zoom In" from there should land on 110%, not skip to 125%. Already past
    // the last step (Ctrl+wheel has no limit) stays put rather than jumping back the other way.
    public static int NextStep(int percent) =>
        Steps.FirstOrDefault(s => s > percent, percent);

    public static int PreviousStep(int percent) =>
        Steps.LastOrDefault(s => s < percent, percent);
}
