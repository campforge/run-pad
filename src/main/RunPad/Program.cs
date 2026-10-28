using Avalonia;
using Avalonia.ReactiveUI;
using RunPad.Services.Desktop;

namespace RunPad;

internal sealed class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        // The platform, and what each OS sets up before Avalonia starts, come from its desktop
        // integration (RunPad.Desktop.Linux, .MacOS, .Windows).
        => DesktopIntegration.Configure(AppBuilder.Configure<App>())
            .WithInterFont()
            .LogToTrace()
            .UseReactiveUI();
}
