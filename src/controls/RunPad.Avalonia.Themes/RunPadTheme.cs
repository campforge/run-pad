using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace RunPad.Avalonia.Themes;

public class RunPadTheme : Styles
{
    public RunPadTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
