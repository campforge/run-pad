using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace Invoklet.Resources;

public sealed class PopupStyles : Styles
{
    public PopupStyles() => AvaloniaXamlLoader.Load(this);
}
