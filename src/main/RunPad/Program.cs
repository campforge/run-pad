using Avalonia;
using Avalonia.ReactiveUI;
using RunPad.Services.Desktop;
using RunPad.Shared;

namespace RunPad;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Before AppKit starts: it reads these switches when it builds the menu bar.
        MacOSEditMenuItems.Disable();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => DesktopSession.Configure(AppBuilder.Configure<App>())
            .WithInterFont()
            // The macOS application menu stays as App.CreateApplicationMenu leaves it, without the
            // Hide/Show All/Quit items Avalonia appends: Quit is File → Exit, as on other systems.
            .With(new MacOSPlatformOptions { DisableDefaultApplicationMenuItems = true })
            .LogToTrace()
            .UseReactiveUI();
}
