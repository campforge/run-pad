using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace RunPad.Windows;

/// <summary>Common native window behavior for compact application dialogs.</summary>
public class RunPadDialogWindow : Window
{
    protected override Type StyleKeyOverride => typeof(Window);

    public RunPadDialogWindow()
    {
        Width = 460;
        MinWidth = 320;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Keep OS-owned decorations, movement and closing behavior on every platform.
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://RunPad/"))
        {
            Source = new Uri("avares://RunPad.Avalonia.Themes/Metrics.axaml"),
        });
    }
}
