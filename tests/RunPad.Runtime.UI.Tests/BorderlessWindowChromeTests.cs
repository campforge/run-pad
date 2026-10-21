using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using RunPad.Avalonia.Themes;
using RunPad.UserControls;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class BorderlessWindowChromeTests
{
    [AvaloniaTheory]
    [InlineData("splash", false)]
    [InlineData("breakpoint", false)]
    [InlineData("goto", false)]
    [InlineData("splash", true)]
    [InlineData("breakpoint", true)]
    [InlineData("goto", true)]
    public void WindowTransparency_ShouldSelectThemedSurface_AndTrackPalette(string kind, bool transparent)
    {
        var theme = new RunPadTheme();
        global::Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window();
        Window window = kind switch
        {
            "splash" => new SplashWindow(),
            "breakpoint" => new BreakpointConditionDialog(new BreakpointConditionEditor(null, null, true,
                (_, _) => Task.FromResult<RunPad.Contracts.IntelliSense.BreakpointConditionAnalysis?>(null)), owner),
            _ => new GoToLineDialog(1, 10)
        };
        try
        {
            window.TransparencyLevelHint = [transparent ? WindowTransparencyLevel.Transparent : WindowTransparencyLevel.None];
            window.Show();
            Assert.Equal(transparent ? WindowTransparencyLevel.Transparent : WindowTransparencyLevel.None, window.ActualTransparencyLevel);
            foreach (var variant in new[] { RunPadThemeVariants.RiderDark, RunPadThemeVariants.RiderLight,
                RunPadThemeVariants.RunPadMidnight, RunPadThemeVariants.RunPadWarmLight,
                RunPadThemeVariants.VsCodeDark, RunPadThemeVariants.VsCodeLight,
                RunPadThemeVariants.Vs2022Dark, RunPadThemeVariants.Vs2022Light })
            {
                window.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                var surface = window.FindControl<Border>("DialogSurface")!;
                theme.TryGetResource("RunPad.Panel.BackgroundBrush", variant, out var expected);
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;
                Assert.Equal(transparent ? Colors.Transparent : Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color,
                    background);
                Assert.Equal(transparent ? new CornerRadius(kind == "splash" ? 12 : 8) : default,
                    surface.CornerRadius);
            }
        }
        finally
        {
            window.Close();
            owner.Close();
            global::Avalonia.Application.Current!.Styles.Remove(theme);
        }
    }
}
