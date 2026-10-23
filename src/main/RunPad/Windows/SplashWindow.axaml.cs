using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RunPad.Windows;

internal sealed partial class SplashWindow : Window
{
    private readonly CancellationTokenSource _revealCancellation = new();

    public SplashWindow()
    {
        InitializeComponent();
        BorderlessWindowChrome.Attach(this, DialogSurface);
        Opacity = 0;
        Opened += RevealAfterDelay;
        Closed += (_, _) =>
        {
            HasClosed = true;
            _revealCancellation.Cancel();
        };
    }

    internal bool HasClosed { get; private set; }

    internal void SetStartupStatus(string status) => StatusText.Text = status;

    internal void ShowStartupError()
    {
        Opacity = 1;
        SizeToContent = SizeToContent.Height;
        StartupErrorText.Text = $"RunPad couldn't finish starting. Logs: {Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RunPad", "logs")}";
        StatusText.IsVisible = false;
        StartupProgress.IsVisible = false;
        StartupError.IsVisible = true;
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => Close();

    private async void RevealAfterDelay(object? sender, EventArgs e)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250), _revealCancellation.Token);
            if (IsVisible) Opacity = 1;
        }
        catch (OperationCanceledException) when (_revealCancellation.IsCancellationRequested)
        {
        }
    }
}
