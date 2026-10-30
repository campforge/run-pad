using Invoklet.Contracts.Debugging;

namespace Invoklet.Debug;

public sealed class DapVariableInfo : IVariableInfo
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string TypeName { get; init; }
    public bool HasChildren { get; init; }
    public int VariablesReference { get; init; }
    public IReadOnlyList<IVariableInfo> Children { get; init; } = [];
}
