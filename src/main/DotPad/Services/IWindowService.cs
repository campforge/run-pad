using DotPad.Format.RPad;

namespace DotPad.Services;

public interface IWindowService
{
    // Takes a live stream rather than a snapshot so the dialog picks up front matter that becomes
    // available after it's already open (see EditorTabItemViewModel.FrontMatter).
    Task ShowDependenciesAsync(IObservable<RpadFrontMatter?>? frontMatter = null);
    Task ShowPreferencesAsync();
    Task ShowErrorAsync(string title, string message);
    Task ShowAboutAsync();
}
