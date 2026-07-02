using DotPad.Contracts;
using DotPad.Contracts.Models;

namespace DotPad.Languages.CSharp.Debug;

internal sealed class DapVariableInfo : IVariableInfo
{
    public required string Name { get; init; }
    public required string Value { get; init; }
    public required string TypeName { get; init; }
    public bool HasChildren { get; init; }
    public IReadOnlyList<IVariableInfo> Children { get; init; } = [];
}
