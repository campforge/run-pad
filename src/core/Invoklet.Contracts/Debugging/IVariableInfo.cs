namespace Invoklet.Contracts.Models;

public interface IVariableInfo
{
    string Name { get; }
    string Value { get; }
    string TypeName { get; }
    bool HasChildren { get; }
    int VariablesReference { get; }
    IReadOnlyList<IVariableInfo> Children { get; }
}
