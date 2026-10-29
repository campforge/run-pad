using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using Moq;
using Invoklet.Windows;
using Xunit;

namespace Invoklet.Runtime.UI.Tests;

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

    // The workspace opens its restored tabs one by one: the splash stays, saying so, and the window
    // stays hidden until every header is there, so tabs never visibly appear one after another.
    [AvaloniaFact]
    public async Task Startup_Should_KeepTheSplash_UntilTheWorkspaceIsReady()
    {
        var splash = new SplashWindow();
        var destination = new SlowStartupWindow();
        var desktop = new Mock<IClassicDesktopStyleApplicationLifetime>();
        desktop.SetupProperty(d => d.MainWindow, splash);
        splash.Show();
        await App.StartDesktopAsync(desktop.Object, splash, () => Task.FromResult(true), _ => destination, Mock.Of<ILogger>());
        Dispatcher.UIThread.RunJobs();

        Assert.False(splash.HasClosed);
        Assert.Equal("Opening scripts…", splash.FindControl<TextBlock>("StatusText")!.Text);
        Assert.Equal(0, destination.Opacity);

        destination.Workspace.SetResult();
        for (var i = 0; i < 20 && !splash.HasClosed; i++) { await Task.Yield(); Dispatcher.UIThread.RunJobs(); }
        Assert.True(splash.HasClosed);
        Assert.Equal(1, destination.Opacity);
        destination.Close();
    }

    private sealed class SlowStartupWindow : Window, IStartupWindow
    {
        public TaskCompletionSource Workspace { get; } = new();
        public Task Ready => Workspace.Task;
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
        Assert.Contains(Path.Combine("Invoklet", "logs"), splash.FindControl<TextBlock>("StartupErrorText")!.Text);
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
        Assert.Equal("Starting Invoklet…", splash.FindControl<TextBlock>("StatusText")!.Text);
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
