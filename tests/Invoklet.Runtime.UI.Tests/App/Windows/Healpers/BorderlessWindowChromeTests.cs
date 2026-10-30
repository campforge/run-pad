using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Invoklet.Avalonia.Themes;
using Invoklet.UserControls;
using Invoklet.Windows;
using Xunit;

namespace Invoklet.Runtime.UI.Tests.App.Windows.Healpers;

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
        var theme = new InvokletTheme();
        global::Avalonia.Application.Current!.Styles.Add(theme);
        var owner = new Window();
        Window window = kind switch
        {
            "splash" => new SplashWindow(),
            "breakpoint" => new BreakpointConditionDialog(new BreakpointConditionEditor(null, null, true,
                (_, _) => Task.FromResult<Invoklet.Contracts.IntelliSense.BreakpointConditionAnalysis?>(null)), owner),
            _ => new GoToLineDialog(1, 10)
        };
        try
        {
            window.TransparencyLevelHint = [transparent ? WindowTransparencyLevel.Transparent : WindowTransparencyLevel.None];
            window.Show();
            Assert.Equal(transparent ? WindowTransparencyLevel.Transparent : WindowTransparencyLevel.None, window.ActualTransparencyLevel);
            foreach (var variant in new[] { InvokletThemeVariants.RiderDark, InvokletThemeVariants.RiderLight,
                InvokletThemeVariants.InvokletMidnight, InvokletThemeVariants.InvokletWarmLight,
                InvokletThemeVariants.VsCodeDark, InvokletThemeVariants.VsCodeLight,
                InvokletThemeVariants.Vs2022Dark, InvokletThemeVariants.Vs2022Light })
            {
                window.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                var surface = window.FindControl<Border>("DialogSurface")!;
                theme.TryGetResource("Invoklet.Panel.BackgroundBrush", variant, out var expected);
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;
                Assert.Equal(transparent ? Colors.Transparent : Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color,
                    background);
                theme.TryGetResource("Invoklet.Radius.Overlay", null, out var overlay);
                Assert.Equal(transparent ? (CornerRadius)overlay! : default,
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
