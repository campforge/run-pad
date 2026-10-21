using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class SplashWindowTests
{
    [AvaloniaFact]
    public void Startup_Should_ShowProgressAndAllowStatusUpdates()
    {
        var splash = new SplashWindow();
        splash.Show();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(0, splash.Opacity);
        Assert.Equal("Starting RunPad…", splash.FindControl<TextBlock>("StatusText")!.Text);
        Assert.True(splash.FindControl<ProgressBar>("StartupProgress")!.IsIndeterminate);

        splash.SetStartupStatus("Checking .NET SDK and debugger…");

        Assert.Equal("Checking .NET SDK and debugger…", splash.FindControl<TextBlock>("StatusText")!.Text);
        splash.Close();
    }

    [AvaloniaFact]
    public void StartupFailure_ShouldReplaceProgressWithCloseMessage()
    {
        var splash = new SplashWindow();
        splash.Show();
        Dispatcher.UIThread.RunJobs();

        splash.ShowStartupError();

        Assert.False(splash.FindControl<TextBlock>("StatusText")!.IsVisible);
        Assert.False(splash.FindControl<ProgressBar>("StartupProgress")!.IsVisible);
        Assert.True(splash.FindControl<StackPanel>("StartupError")!.IsVisible);
        Assert.NotNull(splash.FindControl<Button>("CloseStartupErrorButton"));
        splash.Close();
    }
}
