using DotPad.ViewModels;
using DotPad.Views;

namespace DotPad.Services;

public interface IEditorTabFactory
{
    EditorTabControl Create(EditorTabViewModel vm);
}
