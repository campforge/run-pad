using DotPad.Format.RPad;

namespace DotPad.Services;

public interface IWindowService
{
    Task ShowDependenciesAsync(RpadFrontMatter? frontMatter = null);
    Task ShowPreferencesAsync();
    Task ShowErrorAsync(string title, string message);
}
