namespace Invoklet.Services.Tabs;

/// <summary>Decides which loaded tabs give up their view when there are too many.</summary>
public interface IResidencyPolicy
{
    /// <summary>The user opened or selected <paramref name="tab"/>.</summary>
    void Touch(object tab);

    void Forget(object tab);

    /// <summary>
    /// Of the <paramref name="loaded"/> tabs, those to unload, only among the ones <paramref name="canUnload"/>
    /// accepts (a tab that is running or holds unsaved work stays, however long ago it was used).
    /// </summary>
    IReadOnlyList<T> ColdTabs<T>(IEnumerable<T> loaded, Func<T, bool> canUnload) where T : notnull;
}
