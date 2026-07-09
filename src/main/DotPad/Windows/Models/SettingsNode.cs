namespace DotPad.Windows.Models;

public sealed class SettingsNode(string id, string name, IReadOnlyList<SettingsNode>? children = null)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public IReadOnlyList<SettingsNode> Children { get; } = children ?? [];
}
