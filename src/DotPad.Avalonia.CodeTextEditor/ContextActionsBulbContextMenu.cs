using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Styling;
using DotPad.Avalonia.CodeTextEditor.Shared;

namespace DotPad.Avalonia.CodeTextEditor;

internal class ContextActionsBulbContextMenu : MenuFlyout
{
    private readonly ActionCommandConverter _converter;

    public ContextActionsBulbContextMenu(ActionCommandConverter converter)
    {
        _converter = converter;
        Placement = PlacementMode.Right;
    }

    private Style CreateItemContainerStyle() => new(s => s.OfType<MenuItem>())
    {
        Setters =
        {
            new Setter(MenuItem.CommandProperty, new Binding { Converter = _converter })
        }
    };

    protected override Control CreatePresenter()
    {
        var presenter = base.CreatePresenter();
        presenter.Styles.Add(CreateItemContainerStyle());
        return presenter;
    }
}
