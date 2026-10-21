using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RunPad.Windows;

internal sealed partial class SplashWindow : Window
{
    private readonly CancellationTokenSource _revealCancellation = new();

    public SplashWindow()
    {
        InitializeComponent();
        Opacity = 0;
        Opened += RevealAfterDelay;
        Closed += (_, _) => _revealCancellation.Cancel();
    }

    internal void SetStartupStatus(string status) => StatusText.Text = status;

    internal void ShowStartupError()
    {
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
