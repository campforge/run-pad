using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Invoklet.Avalonia.Themes;

public class InvokletTheme : Styles
{
    public InvokletTheme()
    {
        AvaloniaXamlLoader.Load(this);
    }
}
