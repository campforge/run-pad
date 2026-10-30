using Invoklet.UserControls.Models;

namespace Invoklet.Services.Tabs;

/// <summary>Keeps the released content of tabs somewhere that is not memory.</summary>
public interface ITabSpillStore
{
    /// <summary>False when it could not be written: the content must then stay where it is.</summary>
    bool TryWrite(Guid tab, ReleasedContent content);

    /// <summary>Null when it cannot be read.</summary>
    ReleasedContent? TryRead(Guid tab);

    void Delete(Guid tab);

    /// <summary>Everything this process kept.</summary>
    void DeleteAll();
}
