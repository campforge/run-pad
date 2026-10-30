using Avalonia.Controls;
using Avalonia.Markup.Xaml.Styling;

namespace Invoklet.TemplatedControls;

/// <summary>Common native window behavior for compact application dialogs.</summary>
public class InvokletDialogWindow : Window
{
    protected override Type StyleKeyOverride => typeof(Window);

    public InvokletDialogWindow()
    {
        Width = 460;
        MinWidth = 320;
        CanResize = false;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        // Keep OS-owned decorations, movement and closing behavior on every platform.
        Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Invoklet/"))
        {
            Source = new Uri("avares://Invoklet.Avalonia.Themes/Metrics.axaml"),
        });
    }
}
