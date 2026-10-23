using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Moq;
using RunPad.Windows;
using Xunit;

namespace RunPad.Runtime.UI.Tests;

public class SplashWindowTests
{
    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Startup_ShouldReplaceSplash_AfterPrerequisitesComplete(bool complete)
    {
        var splash = new SplashWindow();
        var destination = new Window();
        var desktop = new Mock<IClassicDesktopStyleApplicationLifetime>();
        desktop.SetupProperty(d => d.MainWindow, splash);
        splash.Show();
        var check = new TaskCompletionSource<bool>();
        var startup = App.StartDesktopAsync(desktop.Object, splash, () => check.Task,
            result => { Assert.Equal(complete, result); return destination; }, Mock.Of<ILogger>());
        Assert.Same(splash, desktop.Object.MainWindow);
        check.SetResult(complete);
        await startup;
        Dispatcher.UIThread.RunJobs();
        Assert.True(destination.IsVisible);
        Assert.Same(destination, desktop.Object.MainWindow);
        Assert.True(splash.HasClosed);
        destination.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartupFailure_ShouldKeepSplashAsMainWindow_AndAllowExit(bool failDuringShow)
    {
        var splash = new SplashWindow();
        var desktop = new Mock<IClassicDesktopStyleApplicationLifetime>();
        desktop.SetupProperty(d => d.MainWindow, splash);
        var closedMainWindow = false;
        splash.Closed += (_, _) => closedMainWindow = ReferenceEquals(desktop.Object.MainWindow, splash);
        splash.Show();
        var failedWindow = new FailingStartupWindow();
        var logger = new Mock<ILogger>();
        await App.StartDesktopAsync(desktop.Object, splash, () => Task.FromResult(true),
            _ => failDuringShow ? failedWindow : throw new InvalidOperationException("construction failed"), logger.Object);
        Dispatcher.UIThread.RunJobs();
        Assert.Same(splash, desktop.Object.MainWindow);
        Assert.True(splash.FindControl<StackPanel>("StartupError")!.IsVisible);
        Assert.Equal(1, splash.Opacity);
        Assert.False(failedWindow.IsVisible);
        Assert.Contains(Path.Combine("RunPad", "logs"), splash.FindControl<TextBlock>("StartupErrorText")!.Text);
        Assert.Single(logger.Invocations, i => i.Method.Name == nameof(ILogger.Log));
        splash.FindControl<Button>("CloseStartupErrorButton")!.RaiseEvent(
            new global::Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Assert.True(closedMainWindow);
        Assert.True(splash.HasClosed);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ExitWhileChecking_ShouldNotCreateWindowOrReportStartupFailure(bool checkFails)
    {
        var splash = new SplashWindow();
        splash.Show();
        var check = new TaskCompletionSource<bool>();
        var logger = new Mock<ILogger>();
        var create = new Mock<Func<bool, Window>>(MockBehavior.Strict);
        var startup = App.StartDesktopAsync(Mock.Of<IClassicDesktopStyleApplicationLifetime>(), splash,
            () => check.Task, create.Object, logger.Object);
        splash.Close();
        if (checkFails) check.SetException(new ObjectDisposedException("services"));
        else check.SetResult(true);
        await startup;
        Assert.Empty(create.Invocations);
        Assert.Empty(logger.Invocations);
        Assert.False(splash.FindControl<StackPanel>("StartupError")!.IsVisible);
    }

    private sealed class FailingStartupWindow : Window
    {
        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            throw new InvalidOperationException("show failed");
        }
    }

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
