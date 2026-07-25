using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace DotPad.Avalonia.Themes;

public class DotPadTheme : Styles
{
    public DotPadTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
