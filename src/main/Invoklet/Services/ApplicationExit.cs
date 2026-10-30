using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Invoklet.Services.Preferences;
using Invoklet.Windows;

namespace Invoklet.Services;

/// <summary>
/// Quitting Invoklet from anywhere goes through the main window's close, which asks about every unsaved
/// tab and text editor window first (MainWindow.OnClosing) and ends the app once it closes:
/// <list type="bullet">
/// <item>the Exit key (Cmd+Q on macOS) pressed in any other window — Preferences, a text editor
/// window, the Assembly Explorer; the main window handles the key itself, like its other keys;</item>
/// <item>the system's own quit request (Dock → Quit, logging out), which would otherwise close the
/// other windows without asking.</item>
/// </list>
/// </summary>
internal static class ApplicationExit
{
    /// <summary>Starts routing; dispose to stop (tests).</summary>
    public static IDisposable Attach(Func<Window?> mainWindow, IPreferencesService preferences,
        IClassicDesktopStyleApplicationLifetime? lifetime = null)
    {
        var keys = InputElement.KeyDownEvent.AddClassHandler<Window>((window, e) =>
        {
            if (mainWindow() is not { } main || window == main) return;
            if (!preferences.Keyboard.Matches(e, KeyCommands.Exit)) return;
            e.Handled = true;
            main.Close();
        }, RoutingStrategies.Bubble, handledEventsToo: true);

        void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
        {
            // Once the main window has closed, the app may end.
            if (mainWindow() is not MainWindow { IsVisible: true } main) return;
            e.Cancel = true;
            Dispatcher.UIThread.Post(main.Close);
        }

        if (lifetime is not null) lifetime.ShutdownRequested += OnShutdownRequested;
        return new Detach(() =>
        {
            keys.Dispose();
            if (lifetime is not null) lifetime.ShutdownRequested -= OnShutdownRequested;
        });
    }

    private sealed class Detach(Action action) : IDisposable
    {
        public void Dispose() => action();
    }
}
